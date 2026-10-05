using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Activity;
using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Interceptors;
using DeChimex.Persistence.Queries;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

using Xunit;
namespace DeChimex.IntegrationTests;

// Test-only provider adaptation. Production stays SQL Server; production entity mappings and
// interceptor are used, only SQL Server's nvarchar(max) syntax is changed for SQLite DDL.
public sealed class ActivitySqliteModelCustomizer(ModelCustomizerDependencies dependencies) : ModelCustomizer(dependencies)
{
    public override void Customize(ModelBuilder builder,DbContext context)
    {
        base.Customize(builder,context);
        foreach(var p in builder.Model.GetEntityTypes().SelectMany(e=>e.GetProperties()))
            if(p.GetColumnType()?.Contains("(max)")==true)p.SetColumnType("TEXT");
    }
}
public class BusinessActivityRelationalTests
{
    private static DeChimexDbContext Open(string file)
    {
        var user=new ActivitySystemUser(); var clock=new ActivityClock();
        return new DeChimexDbContext(new DbContextOptionsBuilder<DeChimexDbContext>()
            .UseSqlite($"Data Source={file};Foreign Keys=True;Pooling=False")
            .ReplaceService<IModelCustomizer,ActivitySqliteModelCustomizer>()
            .AddInterceptors(new AuditableSoftDeleteInterceptor(user,clock)).Options);
    }
    [Fact]
    public async Task AtomicFailure_Retry_Rollback_AndDurableHistory()
    {
        var path=Path.Combine(Path.GetTempPath(),$"activity-{Guid.NewGuid():N}.db");
        try
        {
            Guid successful;
            await using(var db=Open(path))
            {
                await db.Database.EnsureCreatedAsync();
                var first=MajorCategory.Create("Durable Group");db.Add(first);await db.SaveChangesAsync();successful=first.Id;
                var conflict=MajorCategory.Create("Durable Group");db.Add(conflict);
                Func<Task> save=()=>db.SaveChangesAsync();await save.Should().ThrowAsync<DbUpdateException>();
                await using(var check=Open(path))
                    (await check.AuditLogs.CountAsync(a=>a.ActivityType=="major_category.created")).Should().Be(1);
                // Same context retry: no stale success record left over from failed save.
                conflict.Update("Corrected Group",null);await db.SaveChangesAsync();
                (await db.AuditLogs.CountAsync(a=>a.EntityId==conflict.Id.ToString()&&a.ActivityType!=null)).Should().Be(1);
            }
            await using(var db=Open(path))
            {
                var q=new ActivityQueries(db);
                var history=await q.SearchAsync(new ActivityFilter { Search="Durable Group" },default);
                history.Items.Should().ContainSingle();history.Items[0].EntityId.Should().Be(successful.ToString());
                await using(var tx=await db.Database.BeginTransactionAsync())
                {
                    db.Add(MajorCategory.Create("Rolled back"));await db.SaveChangesAsync();await tx.RollbackAsync();
                }
            }
            await using(var db=Open(path))
            {
                (await db.AuditLogs.CountAsync(a=>a.Summary!=null&&a.Summary.Contains("Rolled back"))).Should().Be(0);
                (await db.MajorCategories.CountAsync()).Should().Be(2);
                var row=await db.AuditLogs.FirstAsync(a=>a.SourceKey!=null);
                var duplicate=AuditLog.Create("update","Test");duplicate.CopyBusinessFields(row);db.Add(duplicate);
                Func<Task> save=()=>db.SaveChangesAsync();await save.Should().ThrowAsync<DbUpdateException>();
            }
        }
        finally{if(File.Exists(path))File.Delete(path);}
    }
}

public sealed class ActivityClock : IDateTimeProvider { public DateTime UtcNow => DateTime.UtcNow; }
public sealed class ActivitySystemUser : ICurrentUser
{
 public Guid? UserId=>null;public string? Email=>null;public bool IsAuthenticated=>false;
 public IReadOnlyList<string> Roles=>Array.Empty<string>();public IReadOnlyList<string> Permissions=>Array.Empty<string>();
 public string? IpAddress=>null;public bool HasPermission(string permission)=>false;
}
