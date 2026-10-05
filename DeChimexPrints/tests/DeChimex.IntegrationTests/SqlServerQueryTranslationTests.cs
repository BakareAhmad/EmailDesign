using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Common.Models;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Queries;
using DeChimex.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace DeChimex.IntegrationTests;

/// <summary>
/// SQL-SERVER QUERY TRANSLATION GUARD.
///
/// Every other test in this suite runs the app on the InMemory provider, which
/// evaluates LINQ in .NET and never translates it to SQL. That hides an entire
/// bug class: expressions that compile and pass InMemory tests but throw
/// InvalidOperationException ("could not be translated") the first time the
/// app runs against SQL Server (see AI_HANDOFF §85: ProductRepository.
/// SlugExistsAsync compared p.Slug.Value == slug — a member access on a
/// value-converted value object inside a PREDICATE — and crashed every
/// product create on the owner's machine).
///
/// These tests exercise the REAL query classes against the SQL Server query
/// provider using a deliberately unreachable connection. LINQ translation
/// happens BEFORE any connection attempt, so the discrimination is exact and
/// needs no database:
///   - InvalidOperationException "could not be translated" -> test failure;
///   - any connection error (SqlException/network/timeout)   -> the
///     expression translated fine, the test passes.
///
/// Add a probe here whenever a new query class or non-trivial LINQ shape is
/// introduced in Persistence.
/// </summary>
public class SqlServerQueryTranslationTests
{
    // Port 1 (tcpmux) never runs SQL Server; Connect Timeout=1 bounds any odd
    // environment. We only ever need the connection to FAIL.
    private const string ProbeConnection =
        "Server=127.0.0.1,1;Database=translation_probe;User Id=probe;Password=probe;Connect Timeout=1;TrustServerCertificate=True";

    private static readonly Lazy<DbContextOptions<DeChimexDbContext>> Options = new(() =>
        new DbContextOptionsBuilder<DeChimexDbContext>()
            .UseSqlServer(ProbeConnection)
            .Options);

    private static readonly PassThroughCache Cache = new();

    private static DeChimexDbContext NewDb() => new(Options.Value);

    private static async Task AssertTranslates(string name, Func<DeChimexDbContext, Task> probe)
    {
        using var db = NewDb();
        try
        {
            await probe(db);
            throw new Xunit.Sdk.XunitException(
                $"{name}: unexpectedly completed without a database — probe needs review");
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("could not be translated"))
        {
            throw new Xunit.Sdk.XunitException(
                $"{name}: LINQ expression does not translate for SQL Server.{Environment.NewLine}{ex.Message}");
        }
        catch (Microsoft.Data.SqlClient.SqlException)
        {
            // Connection failure (SqlException/network/timeout): translation itself
            // succeeded — exactly what we are verifying.
        }
    }

    private static async Task AssertAllTranslate(
        params (string Name, Func<DeChimexDbContext, Task> Probe)[] probes)
    {
        var failures = new List<string>();
        foreach (var (name, probe) in probes)
        {
            try { await AssertTranslates(name, probe); }
            catch (Xunit.Sdk.XunitException ex) { failures.Add(ex.Message); }
        }
        failures.Should().BeEmpty("every Persistence LINQ query must translate for SQL Server");
    }

    [Fact]
    public Task MajorCategory_queries_and_combined_filters_translate_for_sql_server()
        => AssertAllTranslate(
            ("Major categories", db => new MajorCategoryQueries(db).GetAllAsync(default)),
            ("Major max order", db => new MajorCategoryRepository(db).GetMaxDisplayOrderAsync()),
            ("Major duplicate check", db => new MajorCategoryRepository(db).ExistsAsync(m => m.NormalizedName == "DRINKWARE")),
            ("Major membership", db => new CategoryRepository(db).ExistsAsync(c => c.MajorCategoryId == Guid.Empty)),
            ("Combined filters", db => new ProductQueries(db).SearchAsync(new ProductFilter { MajorCategoryId = Guid.NewGuid(), CategoryId = Guid.NewGuid(), MinPrice = 1, MaxPrice = 1000, Search = "Mug", SortBy = "price" }, default)));

    // ------------------------------------------------------------------ repos

    [Fact]
    public Task ProductRepository_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("SlugExistsAsync (§85 regression)", db => new ProductRepository(db).SlugExistsAsync("test-slug")),
            ("SkuExistsAsync", db => new ProductRepository(db).SkuExistsAsync("TEST")),
            ("GetWithImagesAsync", db => new ProductRepository(db).GetWithImagesAsync(Guid.NewGuid())));

    [Fact]
    public Task CategoryRepository_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("NameExistsAsync", db => new CategoryRepository(db).NameExistsAsync("test")),
            ("GetMaxDisplayOrderAsync", db => new CategoryRepository(db).GetMaxDisplayOrderAsync()),
            ("GetWithOptionsAsync", db => new CategoryRepository(db).GetWithOptionsAsync(Guid.NewGuid())));

    [Fact]
    public Task InventoryRepository_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("GetByProductIdAsync", db => new InventoryRepository(db).GetByProductIdAsync(Guid.NewGuid())),
            ("GetLowStockAsync", db => new InventoryRepository(db).GetLowStockAsync()));

    // --------------------------------------------------------- catalog queries

    [Fact]
    public Task Catalog_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("CategoryQueries.GetAllAsync(true)",
                db => new CategoryQueries(db, Cache).GetAllAsync(true, CancellationToken.None)),
            ("CategoryQueries.GetAllAsync(false)",
                db => new CategoryQueries(db, Cache).GetAllAsync(false, CancellationToken.None)),
            ("CategoryQueries.GetByIdAsync",
                db => new CategoryQueries(db, Cache).GetByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("ProductQueries.SearchAsync(default)",
                db => new ProductQueries(db).SearchAsync(new ProductFilter { Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("ProductQueries.SearchAsync(activeOnly+featured)",
                db => new ProductQueries(db).SearchAsync(new ProductFilter { ActiveOnly = true, IsFeatured = true, Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("ProductQueries.SearchAsync(search+price)",
                db => new ProductQueries(db).SearchAsync(new ProductFilter { Search = "a", MinPrice = 1, MaxPrice = 2, Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("ProductQueries.SearchAsync(sort=price)",
                db => new ProductQueries(db).SearchAsync(new ProductFilter { SortBy = "price", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("ProductQueries.GetByIdAsync",
                db => new ProductQueries(db).GetByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("ProductQueries.GetFeaturedAsync",
                db => new ProductQueries(db).GetFeaturedAsync(8, CancellationToken.None)),
            ("ProductQueries.GetSkusAsync",
                db => new ProductQueries(db).GetSkusAsync(CancellationToken.None)),
            ("ServiceQueries.GetAllAsync(true)",
                db => new ServiceQueries(db, Cache).GetAllAsync(true, CancellationToken.None)),
            ("ServiceQueries.GetAllAsync(false)",
                db => new ServiceQueries(db, Cache).GetAllAsync(false, CancellationToken.None)),
            ("ServiceQueries.GetByIdAsync",
                db => new ServiceQueries(db, Cache).GetByIdAsync(Guid.NewGuid(), CancellationToken.None)));

    // ------------------------------------------------------ user-facing lookups

    [Fact]
    public Task Customer_user_and_sales_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("CustomerQueries.SearchAsync(search)",
                db => new CustomerQueries(db).SearchAsync(new PagedRequest { Search = "a", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("CustomerQueries.GetByIdAsync",
                db => new CustomerQueries(db).GetByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("UserQueries.SearchAsync(search)",
                db => new UserQueries(db).SearchAsync(new PagedRequest { Search = "a", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("UserQueries.GetByIdAsync",
                db => new UserQueries(db).GetByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("UserQueries.GetRolesAsync",
                db => new UserQueries(db).GetRolesAsync(CancellationToken.None)),
            ("UserQueries.GetPermissionsAsync",
                db => new UserQueries(db).GetPermissionsAsync(CancellationToken.None)),
            ("SalesInboxQueries.GetEligibleSalesStaffAsync",
                db => new SalesInboxQueries(db).GetEligibleSalesStaffAsync(CancellationToken.None)));

    [Fact]
    public Task Invoice_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("InvoiceQueries.GetDocumentByIdAsync",
                db => new InvoiceQueries(db).GetDocumentByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("InvoiceQueries.GetDocumentByOrderIdAsync",
                db => new InvoiceQueries(db).GetDocumentByOrderIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("InvoiceQueries.GetDocumentByShareTokenAsync",
                db => new InvoiceQueries(db).GetDocumentByShareTokenAsync("tok", CancellationToken.None)),
            ("InvoiceQueries.GetReceiptByIdAsync",
                db => new InvoiceQueries(db).GetReceiptByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("PaymentQueries.GetForCustomerAsync",
                db => new PaymentQueries(db).GetForCustomerAsync(Guid.NewGuid(), CancellationToken.None)),
            ("PaymentQueries.GetInvoiceByOrderIdAsync",
                db => new PaymentQueries(db).GetInvoiceByOrderIdAsync(Guid.NewGuid(), CancellationToken.None)));

    // -------------------------------------------------- operations & workflows

    [Fact]
    public Task Contact_and_custom_order_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("ContactMessageQueries.SearchAsync(search, unresolvedOnly)",
                db => new ContactMessageQueries(db).SearchAsync(new PagedRequest { Search = "a", Page = 1, PageSize = 10 }, true, CancellationToken.None)),
            ("ContactMessageQueries.SearchAsync(plain)",
                db => new ContactMessageQueries(db).SearchAsync(new PagedRequest { Page = 1, PageSize = 10 }, false, CancellationToken.None)),
            ("ContactMessageQueries.GetAsync",
                db => new ContactMessageQueries(db).GetAsync(Guid.NewGuid(), CancellationToken.None)),
            ("CategoryOptionsQueries.GetForCategoryAsync",
                db => new CategoryOptionsQueries(db).GetForCategoryAsync(Guid.NewGuid(), CancellationToken.None)),
            ("CategoryOptionsQueries.CategoryHasActiveOptionsAsync",
                db => new CategoryOptionsQueries(db).CategoryHasActiveOptionsAsync(Guid.NewGuid(), CancellationToken.None)),
            ("OrderWorkspaceQueries.GetAsync",
                db => new OrderWorkspaceQueries(db).GetAsync(Guid.NewGuid(), CancellationToken.None)));

    [Fact]
    public Task Dashboard_and_marketing_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("DashboardQueries.GetKpisAsync",
                db => new DashboardQueries(db, new RevenueQueries(db)).GetKpisAsync(CancellationToken.None)),
            ("DashboardQueries.GetRevenueTrendAsync(12)",
                db => new DashboardQueries(db, new RevenueQueries(db)).GetRevenueTrendAsync(12, CancellationToken.None)),
            ("DashboardQueries.GetSalesByCategoryAsync",
                db => new DashboardQueries(db, new RevenueQueries(db)).GetSalesByCategoryAsync(CancellationToken.None)),
            ("MarketingQueries.GetBroadcastsAsync(search)",
                db => new MarketingQueries(db).GetBroadcastsAsync(new PagedRequest { Search = "a", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("MarketingQueries.GetBroadcastsAsync(plain)",
                db => new MarketingQueries(db).GetBroadcastsAsync(new PagedRequest { Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("MarketingQueries.GetDetailsAsync",
                db => new MarketingQueries(db).GetDetailsAsync(Guid.NewGuid(), CancellationToken.None)),
            ("MarketingQueries.GetEligibleRecipientCountAsync",
                db => new MarketingQueries(db).GetEligibleRecipientCountAsync(CancellationToken.None)));

    [Fact]
    public Task Inventory_order_and_catalog_admin_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("InventoryQueries.SearchAsync(search)",
                db => new InventoryQueries(db).SearchAsync(new PagedRequest { Search = "a", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("InventoryQueries.GetByProductIdAsync",
                db => new InventoryQueries(db).GetByProductIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("InventoryQueries.GetHistoryAsync",
                db => new InventoryQueries(db).GetHistoryAsync(Guid.NewGuid(), CancellationToken.None)),
            ("InventoryQueries.GetLowStockAsync",
                db => new InventoryQueries(db).GetLowStockAsync(CancellationToken.None)),
            ("OrderQueries.SearchAsync(default)",
                db => new OrderQueries(db).SearchAsync(new OrderFilter { Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("OrderQueries.SearchAsync(type filter)",
                db => new OrderQueries(db).SearchAsync(new OrderFilter { Type = "custom", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("OrderQueries.SearchAsync(status+customer+dates)",
                db => new OrderQueries(db).SearchAsync(new OrderFilter { Status = "pending", CustomerId = Guid.NewGuid(), FromUtc = DateTime.UtcNow.AddDays(-7), ToUtc = DateTime.UtcNow, Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("OrderQueries.GetByIdAsync",
                db => new OrderQueries(db).GetByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("OrderQueries.GetByNumberAsync",
                db => new OrderQueries(db).GetByNumberAsync("DCX-0001", CancellationToken.None)),
            ("OrderQueries.GetForCustomerAsync",
                db => new OrderQueries(db).GetForCustomerAsync(Guid.NewGuid(), CancellationToken.None)));

    [Fact]
    public Task Gallery_procurement_cart_wishlist_and_notification_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("GalleryQueries.GetAllAsync(includeArchived, category)",
                db => new GalleryQueries(db).GetAllAsync(true, "banner", CancellationToken.None)),
            ("GalleryQueries.GetAllAsync(false, null)",
                db => new GalleryQueries(db).GetAllAsync(false, null, CancellationToken.None)),
            ("ProcurementQueries.SearchAsync(search)",
                db => new ProcurementQueries(db).SearchAsync(new PagedRequest { Search = "a", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("ProcurementQueries.GetByIdAsync",
                db => new ProcurementQueries(db).GetByIdAsync(Guid.NewGuid(), CancellationToken.None)),
            ("CartQueries.GetForCustomerAsync",
                db => new CartQueries(db).GetForCustomerAsync(Guid.NewGuid(), CancellationToken.None)),
            ("WishlistQueries.GetForCustomerAsync",
                db => new WishlistQueries(db).GetForCustomerAsync(Guid.NewGuid(), CancellationToken.None)),
            ("NotificationQueries.GetForUserAsync(unreadOnly)",
                db => new NotificationQueries(db).GetForUserAsync(Guid.NewGuid(), true, new[] { "orders.view" }, true, CancellationToken.None)),
            ("NotificationQueries.GetForUserAsync(all)",
                db => new NotificationQueries(db).GetForUserAsync(Guid.NewGuid(), false, new[] { "orders.view" }, true, CancellationToken.None)),
            ("NotificationQueries.CountUnreadAsync",
                db => new NotificationQueries(db).CountUnreadAsync(Guid.NewGuid(), new[] { "orders.view" }, true, CancellationToken.None)));

    [Fact]
    public Task Sales_inbox_queries_translate_for_sql_server()
        => AssertAllTranslate(
            ("SalesInboxQueries.GetInboxAsync(bucket=new)",
                db => new SalesInboxQueries(db).GetInboxAsync(new SalesInboxFilter { Bucket = "new", Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("SalesInboxQueries.GetInboxAsync(bucket=active+staff)",
                db => new SalesInboxQueries(db).GetInboxAsync(new SalesInboxFilter { Bucket = "active", StaffUserId = Guid.NewGuid(), Page = 1, PageSize = 10 }, CancellationToken.None)),
            ("SalesInboxQueries.GetBucketCountsAsync(staff)",
                db => new SalesInboxQueries(db).GetBucketCountsAsync(Guid.NewGuid(), CancellationToken.None)),
            ("SalesInboxQueries.GetBucketCountsAsync(null)",
                db => new SalesInboxQueries(db).GetBucketCountsAsync(null, CancellationToken.None)));

    /// <summary>Cache passthrough so cached query classes run their inner query.</summary>
    private sealed class PassThroughCache : ICacheService
    {
        public Task<T?> GetAsync<T>(string key, CancellationToken ct = default) => Task.FromResult<T?>(default);
        public Task SetAsync<T>(string key, T value, TimeSpan? absoluteExpiration = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task RemoveAsync(string key, CancellationToken ct = default) => Task.CompletedTask;
        public async Task<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, Task<T>> factory, TimeSpan? absoluteExpiration = null, CancellationToken ct = default)
            => await factory(ct);
    }
}
