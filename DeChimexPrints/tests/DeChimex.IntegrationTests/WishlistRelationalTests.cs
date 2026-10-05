using DeChimex.Domain.Entities;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Interceptors;
using DeChimex.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;
namespace DeChimex.IntegrationTests;
public class WishlistRelationalTests
{
    private static DeChimexDbContext Open(string file)=>new(new DbContextOptionsBuilder<DeChimexDbContext>()
        .UseSqlite($"Data Source={file};Pooling=False")
        .ReplaceService<IModelCustomizer,ActivitySqliteModelCustomizer>()
        .AddInterceptors(new AuditableSoftDeleteInterceptor(new ActivitySystemUser(),new ActivityClock())).Options);
    [Fact]
    public async Task RemoveAndReaddAcrossContexts_ReusesUniqueMembership_AndPersists()
    {
        var path=Path.Combine(Path.GetTempPath(),$"wishlist-{Guid.NewGuid():N}.db");
        var customer=Guid.NewGuid();var product=Guid.NewGuid();Guid row;
        try{
            await using(var db=Open(path)){
                await db.Database.EnsureCreatedAsync();var repo=new WishlistRepository(db);
                var item=WishlistItem.Create(customer,product);row=item.Id;
                await repo.AddAsync(item);await db.SaveChangesAsync();repo.Remove(item);await db.SaveChangesAsync();
                (await repo.FindAsync(customer,product)).Should().BeNull();
            }
            await using(var db=Open(path)){
                var repo=new WishlistRepository(db);await repo.AddAsync(WishlistItem.Create(customer,product));await db.SaveChangesAsync();
                await repo.AddAsync(WishlistItem.Create(customer,product));await db.SaveChangesAsync();
            }
            await using(var db=Open(path)){
                var items=await db.WishlistItems.IgnoreQueryFilters().ToListAsync();items.Should().ContainSingle();
                items[0].Id.Should().Be(row);items[0].IsDeleted.Should().BeFalse();items[0].DeletedDateUtc.Should().BeNull();
                (await new WishlistRepository(db).FindAsync(customer,product)).Should().NotBeNull();
            }
        }finally{File.Delete(path);}
    }
}
