# 04 · SQL Server & EF Core Setup

## SQL Server (local / Docker)

```bash
# Docker (dev)
docker run -e "ACCEPT_EULA=Y" -e "MSSQL_SA_PASSWORD=Your_password123" \
  -p 1433:1433 --name dechimex-sql -d mcr.microsoft.com/mssql/server:2022-latest
```

Connection string (`appsettings.json` / env / secrets):
```
Server=localhost,1433;Database=DeChimexDb;User Id=sa;Password=Your_password123;TrustServerCertificate=True;MultipleActiveResultSets=true
```
> The dev default in `appsettings.json` / `appsettings.Development.json` already points at
> `Server=localhost,1433;Database=DeChimexDb`. Keep the database name `DeChimexDb`.
> In production use a least-privilege SQL login (not `sa`) and `Encrypt=True` with a valid certificate.
>
> **No SQL Server available?** Set `Database:UseInMemory=true` (env: `Database__UseInMemory=true`)
> to run the app against EF Core's InMemory provider. That is a dev/preview/test path only —
> it must never be the production configuration.

## EF Core tooling

The repo pins `dotnet-ef` in `.config/dotnet-tools.json`:
```bash
dotnet tool restore
```

## Migrations

```bash
# Add a migration
dotnet ef migrations add <Name> \
  --project src/DeChimex.Persistence --startup-project src/DeChimex.Host

# Apply to the database
dotnet ef database update \
  --project src/DeChimex.Persistence --startup-project src/DeChimex.Host

# Generate an idempotent SQL script (for DBAs / prod release)
# NOTE: write it somewhere OUTSIDE the repo — the previously committed
# database/migrate.sql was stale and has been removed.
dotnet ef migrations script --idempotent \
  --project src/DeChimex.Persistence --startup-project src/DeChimex.Host \
  --output /tmp/dechimex-release.sql
```

Current migration set (SEVEN, in order — `src/DeChimex.Persistence/Migrations/`):
`20260820005520_initial`, `20260828221847_CustomerGuestSupport`,
`20260828224705_QuoteInvoiceAndReceipts`,
`20260829001357_RemediationOwnerPasswordAndContactMessages`,
`20260829201816_AddNotificationRequiredPermission`,
`20260901202823_DropDormantTables` (drops `QuotationLines`, `RefreshTokens`, `Quotations`),
`20260904214019_RemoveGalleryShowOnHomepage` (drops `GalleryMedia.ShowOnHomepage`).
The initial migration is named `20260820005520_initial.cs` (lowercase — hence the benign
CS8981 build warning); there is no `*_InitialCreate.cs`. Migrations are **additive only**:
never edit a released migration, and keep the model snapshot in sync.

On startup the host calls `DatabaseSeeder.SeedAsync()`, which runs `Database.MigrateAsync()`
(or `EnsureCreated()` on the InMemory provider) and then seeds reference data — so a fresh
environment is ready after the first run. To disable auto-migrate in production, remove the
seeding block from `Program.cs` and apply a generated idempotent script during deployment.

## What gets seeded (idempotent, runs on every startup)
- All permissions (`Permissions.All()` — 35 permission strings) and 6 roles with their
  canonical permission maps (Owner, Manager, SalesStaff, InventoryStaff, Designer, Customer).
  The canonical map is applied only when a role is **first created**; afterwards the admin
  checklist at `/admin/users/roles` is the source of truth and survives restarts.
- **Owner** account (`Seed:OwnerEmail` / `Seed:OwnerPassword`) flagged `MustChangePassword`,
  so the first login is forced through `/admin/security/password?forced=1`.
  **Change the password immediately.**
- Application settings (business name, motto, currency, default delivery fee, VAT rate,
  marketing stats, WhatsApp number…).
- **NO catalog is seeded.** Categories, products, services and gallery start EMPTY by design
  (no demo/mock data). The seeder actively **purges** legacy starter rows left by older builds:
  products `MUG-001/PEN-007/FLK-021/JER-014/NBK-003/GFT-099`, categories
  `Fashion/Gift Items/Office Supplies/School Supplies/Corporate Gifts`, and services
  `Mug Printing/Jersey Printing/Engraving/Vehicle Branding`.
  (Earlier revisions of this page claimed "5 categories, 3 featured products, 4 services"
  were seeded — that has not been true since the starter catalog was retired.)

## Conventions
- Guid PKs. Owned value objects (`Money`, `Email`, `Address`, `Slug`) mapped via `OwnsOne`/converters.
- Unique indexes: user email, role name, product SKU, order/invoice/payment/procurement references,
  category & product slug, inventory per product, wishlist (customer+product).
- Global soft-delete query filters on business aggregates; audit columns auto-populated by an interceptor.
- All mapping lives in `IEntityTypeConfiguration<T>` classes (no Fluent API inside `DbContext`).
