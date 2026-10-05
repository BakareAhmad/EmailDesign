using DeChimex.Domain.Entities;
using DeChimex.Persistence.Configurations;
using DeChimex.Persistence.Migrations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;
namespace DeChimex.IntegrationTests;

/// <summary>Focused real relational schema tests using the production catalog mappings.
/// SQLite validates FKs/unique constraints and durable reopen; it is NOT a SQL Server migration execution test.</summary>
public class MajorCategoryRelationalTests
{
    private sealed class CatalogDb(DbContextOptions<CatalogDb> options) : DbContext(options)
    {
        public DbSet<MajorCategory> Majors => Set<MajorCategory>();
        public DbSet<Category> Categories => Set<Category>();
        public DbSet<Product> Products => Set<Product>();
        protected override void OnModelCreating(ModelBuilder b)
        {
            b.ApplyConfiguration(new MajorCategoryConfiguration());
            b.ApplyConfiguration(new CategoryConfiguration());
            b.ApplyConfiguration(new ProductConfiguration());
            b.ApplyConfiguration(new ProductImageConfiguration());
            // Ordering options are outside this focused relational harness.
            b.Entity<Category>().Ignore(c => c.Options);
            b.Ignore<CategoryOption>(); b.Ignore<CategoryOptionChoice>();
        }
    }
    [Fact]
    public async Task RealForeignKeys_UniqueIndex_AndDurableReopen_PreserveProductsAfterMove()
    {
        var path=Path.Combine(Path.GetTempPath(),$"major-relational-{Guid.NewGuid():N}.db");
        var opts=new DbContextOptionsBuilder<CatalogDb>().UseSqlite($"Data Source={path};Foreign Keys=True;Pooling=False").Options;
        Guid categoryId, productId, targetId;
        try
        {
            await using(var db=new CatalogDb(opts))
            {
                await db.Database.EnsureCreatedAsync();
                var a=MajorCategory.Create("Drinkware"); var b=MajorCategory.Create("Gifts");
                var cat=Category.Create("Mugs"); // legacy unassigned row
                var product=Product.Create("Magic Mug","MUG-00001",null,cat.Id,5000,true,false);
                db.AddRange(a,b,cat,product);await db.SaveChangesAsync();
                cat.AssignMajorCategory(a.Id);await db.SaveChangesAsync();
                categoryId=cat.Id;productId=product.Id;targetId=b.Id;
            }
            // A genuinely new connection/context reads persisted state and moves only the category.
            await using(var db=new CatalogDb(opts))
            {
                var product=await db.Products.SingleAsync(p=>p.Id==productId);
                product.CategoryId.Should().Be(categoryId);
                var cat=await db.Categories.SingleAsync(c=>c.Id==categoryId);
                cat.AssignMajorCategory(targetId);await db.SaveChangesAsync();
                var duplicate=MajorCategory.Create("  gIfTs  ");db.Add(duplicate);
                Func<Task> save=()=>db.SaveChangesAsync();await save.Should().ThrowAsync<DbUpdateException>();db.Entry(duplicate).State=EntityState.Detached;
                Func<Task> deleteParent = () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM MajorCategories WHERE Id = {targetId}");
                await deleteParent.Should().ThrowAsync<Microsoft.Data.Sqlite.SqliteException>();
                cat.AssignMajorCategory(Guid.NewGuid());await save.Should().ThrowAsync<DbUpdateException>();
            }
            await using(var db=new CatalogDb(opts))
            {
                (await db.Products.SingleAsync(p=>p.Id==productId)).CategoryId.Should().Be(categoryId);
                (await db.Categories.SingleAsync(c=>c.Id==categoryId)).MajorCategoryId.Should().Be(targetId);
                // Filtered unique index permits reuse only after soft deletion.
                var empty=await db.Majors.SingleAsync(m=>m.Name=="Drinkware");
                db.Entry(empty).Property("IsDeleted").CurrentValue=true;await db.SaveChangesAsync();
                db.Add(MajorCategory.Create("Drinkware"));await db.SaveChangesAsync();
            }
        }
        finally { if(File.Exists(path)) File.Delete(path); }
    }
    [Fact]
    public void MigrationIsAdditive_AndDoesNotTouchProductData()
    {
        var migration=new AddMajorCategories();
        migration.UpOperations.Should().NotContain(o=>o is DropTableOperation || o is DropColumnOperation || o is DeleteDataOperation || o is UpdateDataOperation);
        migration.UpOperations.OfType<AddColumnOperation>().Should().ContainSingle(c=>c.Table=="Categories" && c.Name=="MajorCategoryId" && c.IsNullable);
        migration.UpOperations.OfType<CreateTableOperation>().Should().ContainSingle(t=>t.Name=="MajorCategories");
        migration.UpOperations.OfType<AddForeignKeyOperation>().Should().ContainSingle(f=>f.Table=="Categories" && f.PrincipalTable=="MajorCategories" && f.OnDelete==Microsoft.EntityFrameworkCore.Migrations.ReferentialAction.Restrict);
    }
}
