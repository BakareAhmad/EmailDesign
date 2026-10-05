using System.Text.Json;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace DeChimex.IntegrationTests;

/// <summary>
/// Relational providers (SQL Server, SQLite) return DateTime values with Kind=Unspecified.
/// Every DateTime the app stores is UTC (all properties are *Utc), so the context marks
/// them Utc on read. Without that, JSON carried no "Z" and browsers parsed the value as
/// LOCAL time — relative times ("5 minutes ago") were skewed by the viewer's UTC offset
/// (1 h in Lagos). The in-memory test provider preserves Kind, which hid the bug.
/// </summary>
public class UtcDateTimeRelationalTests
{
    private static DeChimexDbContext Open(string file) => new(new DbContextOptionsBuilder<DeChimexDbContext>()
        .UseSqlite($"Data Source={file};Pooling=False")
        .ReplaceService<IModelCustomizer, ActivitySqliteModelCustomizer>()
        .AddInterceptors(new AuditableSoftDeleteInterceptor(new ActivitySystemUser(), new ActivityClock())).Options);

    [Fact]
    public async Task DateTimes_read_from_a_relational_database_are_Utc_and_serialize_with_Z()
    {
        var path = Path.Combine(Path.GetTempPath(), $"utc-{Guid.NewGuid():N}.db");
        try
        {
            Guid id;
            await using (var db = Open(path))
            {
                await db.Database.EnsureCreatedAsync();
                var item = WishlistItem.Create(Guid.NewGuid(), Guid.NewGuid());
                id = item.Id;
                db.WishlistItems.Add(item);
                await db.SaveChangesAsync();
                db.WishlistItems.Remove(item); // soft delete -> sets the nullable DeletedDateUtc
                await db.SaveChangesAsync();
            }

            await using (var db = Open(path))
            {
                var row = await db.WishlistItems.IgnoreQueryFilters().SingleAsync(x => x.Id == id);
                row.CreatedDateUtc.Kind.Should().Be(DateTimeKind.Utc);
                row.DeletedDateUtc.Should().NotBeNull();
                row.DeletedDateUtc!.Value.Kind.Should().Be(DateTimeKind.Utc);
                JsonSerializer.Serialize(row.CreatedDateUtc).Should().EndWith("Z\"");

                // Projections (the DTO path used by JSON endpoints) are converted too.
                var projected = await db.WishlistItems.IgnoreQueryFilters().Where(x => x.Id == id)
                    .Select(x => x.CreatedDateUtc).SingleAsync();
                projected.Kind.Should().Be(DateTimeKind.Utc);

                // Filtering against a UTC instant still translates and matches.
                var since = DateTime.UtcNow.AddMinutes(-5);
                (await db.WishlistItems.IgnoreQueryFilters().CountAsync(x => x.CreatedDateUtc >= since)).Should().Be(1);
            }
        }
        finally { File.Delete(path); }
    }
}
