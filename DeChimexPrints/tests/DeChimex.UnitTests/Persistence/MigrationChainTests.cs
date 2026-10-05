using System.Text.RegularExpressions;
using DeChimex.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace DeChimex.UnitTests.Persistence;

/// <summary>
/// Regression guards for the SQL Server migration chain (regeneration of the real DDL via
/// IMigrator.GenerateScript — no database connection is opened).
///
/// Bug being guarded against: 20261004110951_PwaPushSubscriptions originally only diffed an
/// index against a stale snapshot, so a database at 20260925232100 (i.e. any real database)
/// hit "Cannot drop the index 'PushSubscriptions.IX_PushSubscriptions_UserId_Endpoint',
/// because it does not exist or you do not have permission." (SQL error 3701) during
/// Update-Database — the PushSubscriptions table itself was never created by any migration.
/// </summary>
public class MigrationChainTests
{
    private static string Script(string from, string? to = null)
    {
        using var context = new DesignTimeDbContextFactory().CreateDbContext(Array.Empty<string>());
        return context.GetService<IMigrator>().GenerateScript(from, to);
    }

    [Fact]
    public void Upgrade_from_20260925_creates_the_push_table_without_dropping_indexes()
    {
        var hop = Script("20260925232100_AddDeveloperPortalDiagnostics");

        Assert.Contains("CREATE TABLE [PushSubscriptions]", hop);
        Assert.Contains("CREATE INDEX [IX_PushSubscriptions_Endpoint] ON [PushSubscriptions] ([Endpoint]);", hop);
        Assert.Contains(
            "CREATE UNIQUE INDEX [IX_PushSubscriptions_UserId_Endpoint] ON [PushSubscriptions] ([UserId], [Endpoint]) WHERE [IsDeleted] = 0",
            hop);
        Assert.DoesNotContain("DROP INDEX", hop);
    }

    [Fact]
    public void PushSubscriptions_is_created_exactly_once_by_the_full_chain()
    {
        var full = Script("0");

        Assert.Equal(1, Regex.Matches(full, @"CREATE TABLE \[PushSubscriptions\]").Count);
    }

    [Fact]
    public void No_index_or_table_is_dropped_before_its_table_exists_in_the_full_chain()
    {
        var full = Script("0");

        var tableCreates = Regex.Matches(full, @"CREATE TABLE \[([^\]]+)\]")
            .ToDictionary(m => m.Groups[1].Value, m => m.Index);
        Assert.NotEmpty(tableCreates);

        foreach (Match drop in Regex.Matches(full, @"DROP (?:INDEX \[[^\]]+\] ON|TABLE) \[([^\]]+)\]"))
        {
            var table = drop.Groups[1].Value;
            Assert.True(
                tableCreates.TryGetValue(table, out var createdAt) && drop.Index > createdAt,
                $"'{drop.Value}' at offset {drop.Index} executes before/at the creation of table '{table}' " +
                "— a fresh Update-Database would fail with SQL error 3701.");
        }
    }
}
