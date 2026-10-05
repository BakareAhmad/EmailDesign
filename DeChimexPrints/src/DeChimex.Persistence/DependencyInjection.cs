using DeChimex.Application.Common.Interfaces;
using DeChimex.Application.Features.Categories.Queries;
using DeChimex.Application.Features.Users;
using DeChimex.Domain.Repositories;
using DeChimex.Persistence.Context;
using DeChimex.Persistence.Interceptors;
using DeChimex.Persistence.Queries;
using DeChimex.Persistence.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DeChimex.Persistence;

public static class DependencyInjection
{
    public static IServiceCollection AddPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<AuditableSoftDeleteInterceptor>();
        services.AddSingleton(sp => new SlowCommandInterceptor(
            sp.GetRequiredService<ITechnicalEventRecorder>(),
            int.TryParse(configuration["DevPortal:SlowDbCommandMs"], out var slowMs) && slowMs > 0 ? slowMs : 500));

        // Opt-in InMemory provider for local preview/tests without SQL Server.
        // Set "Database:UseInMemory" = true. Absent, the production SqlServer path is used unchanged.
        var useInMemory = string.Equals(configuration["Database:UseInMemory"], "true", StringComparison.OrdinalIgnoreCase);

        services.AddDbContext<DeChimexDbContext>((sp, options) =>
        {
            if (useInMemory)
            {
                options.UseInMemoryDatabase("DeChimexPreview");
            }
            else
            {
                options.UseSqlServer(
                    configuration.GetConnectionString("DefaultConnection"),
                    sql =>
                    {
                        sql.EnableRetryOnFailure(maxRetryCount: 5, maxRetryDelay: TimeSpan.FromSeconds(10), errorNumbersToAdd: null);
                        sql.MigrationsAssembly(typeof(DeChimexDbContext).Assembly.FullName);
                    });
            }
            options.AddInterceptors(sp.GetRequiredService<AuditableSoftDeleteInterceptor>());
            if (!useInMemory)
            {
                // Developer Portal: slow-command capture (relational only; InMemory has no commands).
                options.AddInterceptors(sp.GetRequiredService<SlowCommandInterceptor>());
            }

            // Join-entity navigations are intentionally required while parents carry soft-delete
            // query filters; this is safe here, so silence the informational warning.
            options.ConfigureWarnings(w =>
                w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning));
        });

        services.AddScoped<IUnitOfWork, UnitOfWork>();

        services.AddScoped<IMajorCategoryRepository, MajorCategoryRepository>();
        services.AddScoped<DeChimex.Application.Features.Categories.IMajorCategoryQueries, MajorCategoryQueries>();
        // Repositories
        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IPermissionRepository, PermissionRepository>();
        services.AddScoped<ICategoryRepository, CategoryRepository>();
        services.AddScoped<IProductRepository, ProductRepository>();
        services.AddScoped<IServiceRepository, ServiceRepository>();
        services.AddScoped<IInventoryRepository, InventoryRepository>();
        services.AddScoped<ICustomerRepository, CustomerRepository>();
        services.AddScoped<IOrderRepository, OrderRepository>();
        services.AddScoped<IInvoiceRepository, InvoiceRepository>();
        services.AddScoped<IReceiptRepository, ReceiptRepository>();
        services.AddScoped<IPaymentRepository, PaymentRepository>();
        services.AddScoped<IProcurementRepository, ProcurementRepository>();
        services.AddScoped<IGalleryRepository, GalleryRepository>();
        services.AddScoped<IShoppingCartRepository, ShoppingCartRepository>();
        services.AddScoped<IWishlistRepository, WishlistRepository>();
        services.AddScoped<IReviewRepository, ReviewRepository>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IApplicationSettingRepository, ApplicationSettingRepository>();
        services.AddScoped<IContactMessageRepository, ContactMessageRepository>();
        // IAuditLogWriter/AuditLogWriter registration removed (2026-09-05): dead code.
        // Audit rows are written by AuditableSoftDeleteInterceptor inside SaveChanges.
        services.AddScoped<IReferenceGenerator, ReferenceGenerator>();

        services.AddScoped<DeChimex.Application.Features.Activity.IActivityQueries, ActivityQueries>();
        // Query services (CQRS read side)
        services.AddScoped<ICategoryQueries, CategoryQueries>();
        services.AddScoped<IProductQueries, ProductQueries>();
        services.AddScoped<IServiceQueries, ServiceQueries>();
        services.AddScoped<IInventoryQueries, InventoryQueries>();
        services.AddScoped<ICustomerQueries, CustomerQueries>();
        services.AddScoped<IOrderQueries, OrderQueries>();
        services.AddScoped<IPaymentQueries, PaymentQueries>();
        services.AddScoped<IInvoiceQueries, InvoiceQueries>();
        services.AddScoped<IGalleryQueries, GalleryQueries>();
        services.AddScoped<IProcurementQueries, ProcurementQueries>();
        services.AddScoped<ICartQueries, CartQueries>();
        services.AddScoped<IWishlistQueries, WishlistQueries>();
        services.AddScoped<INotificationQueries, NotificationQueries>();
        services.AddScoped<IDashboardQueries, DashboardQueries>();
        services.AddScoped<IRevenueQueries, RevenueQueries>(); // AUD-001/016: one shared revenue definition
        services.AddScoped<IUploadAccessService, Persistence.Services.UploadAccessService>(); // AUD-021
        services.AddScoped<IPushSubscriptionRepository, PushSubscriptionRepository>(); // PWA Web Push
        services.AddScoped<IUserQueries, UserQueries>();
        services.AddScoped<IContactMessageQueries, ContactMessageQueries>();
        services.AddScoped<IMarketingRepository, MarketingRepository>();
        services.AddScoped<IMarketingQueries, MarketingQueries>();
        services.AddScoped<ICategoryOptionsQueries, CategoryOptionsQueries>();
        services.AddScoped<IOrderWorkspaceQueries, OrderWorkspaceQueries>();
        services.AddScoped<ISalesInboxQueries, SalesInboxQueries>();

        return services;
    }
}
