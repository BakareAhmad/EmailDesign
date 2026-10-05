# DE-CHIMEX PRINT HUB Backend API

> **Creativity with Ease.**
> Production-grade **.NET 9 / ASP.NET Core Web API** for the DE-CHIMEX PRINT HUB
> graphics, branding, printing & promotional-products business.

Built with **Onion Architecture + DDD + CQRS (MediatR) + EF Core + SQL Server**,
JWT auth with refresh tokens, permission-based authorization, Paystack payments,
Brevo email, Serilog, FluentValidation, in-memory caching (Redis-ready), health checks, HSTS, CSP,
and Swagger.

---

## Solution layout (Onion Architecture)

```
DeChimex.sln
└── src/
    ├── DeChimex.Domain          # Entities, aggregates, value objects, domain events, repo interfaces
    ├── DeChimex.Shared          # Cross-cutting primitives (Result, constants, permissions)
    ├── DeChimex.Contracts       # DTOs / request & response models (API contract)
    ├── DeChimex.Application      # CQRS commands/queries, validators, behaviors, service interfaces
    ├── DeChimex.Persistence      # DbContext, EF configurations, repositories, query services, migrations
    ├── DeChimex.Infrastructure   # JWT, hashing, Paystack, Brevo, storage, cache, seeder
    └── DeChimex.Host              # Controllers, middleware, auth, versioning, Swagger, Program.cs
```

**Dependency direction points inward.** Domain depends on nothing (only `MediatR.Contracts`
for the domain-event marker). Application depends on Domain + Contracts + Shared. Persistence &
Infrastructure depend on Application (implementing its interfaces). API composes everything.

---

## Quick start (local)

```bash
# 1. Prerequisites: .NET 9 SDK, SQL Server (or Docker), dotnet-ef
dotnet tool restore                     # restores the pinned dotnet-ef tool

# 2. Configure secrets (see docs/07-configuration.md). At minimum set the
#    ConnectionStrings:DefaultConnection (and any Paystack/Brevo keys you need). No JWT
#    signing key is required — MVC auth uses a cookie, not JWT.

# 3. Apply the database (migrations auto-run on startup, or run manually):
dotnet ef database update \
  --project src/DeChimex.Persistence \
  --startup-project src/DeChimex.Host

# 4. Run
dotnet run --project src/DeChimex.Host
# Swagger UI: https://localhost:5001/swagger
```

On first run the app **migrates and seeds** roles, permissions, an Owner account,
default settings and starter catalog data (5 categories, 3 products with inventory). **Change the seeded Owner password immediately:** `owner@dechimexprinthub.com / ChangeMe123!`.

---

## Verification (gold zip v029)

- **Build:** `dotnet build DeChimex.sln -c Debug` → **0 Warning(s) 0 Error(s)**
- **Tests:** Unit **81 passed**, Integration **54 passed** (InMemory, no SQL Server needed for CI)
- **Frontend:** 53 pages, WCAG 2.1 AA (skip-links, landmarks, ARIA, focus trap, live regions, inline validation, loading skeletons, empty states, breadcrumbs, responsive), `node --check` clean.
- **Security:** HSTS (preload, includeSubDomains, 365d), CSP, X-Content-Type-Options nosniff, X-Frame-Options DENY, Referrer-Policy strict-origin-when-cross-origin, Permissions-Policy, X-DNS-Prefetch-Control off, Server header removed, CORS locked to prod domains (localhost only in Development), no secrets in appsettings.json/Production.json (env vars), Paystack public key via `/config`, rate limiting 10 req/min auth, 100 req/min global.
- **Performance:** N+1 fixed (Cart batch fetch), GrandTotal recomputed in SQL (Items+Services+Delivery+Tax minus Discount, floored), cache for categories/services/settings (10-15min, invalidation on write), static file cache headers `public,max-age=604800` for uploads, response caching/compression, immutable 1y for hashed frontend bundles.
- **Logging:** Serilog Console+File, correlation id `X-Correlation-Id`, Enrich FromLogContext/Machine/Environment, structured logs with method/path/user.

---

## Documentation index (`/docs`)

| # | Document |
|---|----------|
| 01 | [Architecture & flows](docs/01-architecture.md): solution, ER diagram, CQRS, auth, payment, email flows, Onion diagram |
| 02 | [API endpoint reference](docs/02-api-reference.md) |
| 03 | [Sample requests & responses](docs/03-sample-requests.md) |
| 04 | [SQL Server & EF Core setup](docs/04-database-setup.md) |
| 05 | [Brevo (email) setup](docs/05-brevo-setup.md) |
| 06 | [Paystack (payments) setup](docs/06-paystack-setup.md) |
| 07 | [Configuration, JWT, CORS, Serilog, HTTPS, .env](docs/07-configuration.md) |
| 08 | [Deployment: IIS, Docker, Azure, Linux/Nginx, SQL prod, CI/CD](docs/08-deployment.md) |
| 09 | [Production checklists: security, performance, monitoring, backup, scaling, readiness](docs/09-checklists.md) |
| 10 | [Frontend integration guide](docs/10-frontend-integration.md) |
| 11 | [NuGet packages & testing](docs/11-packages-and-testing.md) |
| 12 | [Error handling & error codes](docs/12-error-handling.md) |
| 13 | [API versioning strategy](docs/13-api-versioning.md) |
| 14 | [Troubleshooting](docs/14-troubleshooting.md) |

The original vanilla-JS SPA (`DeChimexPrintsFrontend`) was removed; the app is now a
single server-rendered ASP.NET Core MVC solution (`DeChimexPrints`). Its standalone
frontend docs (accessibility audit, asset bundling, offline-support plan) no longer apply.

Postman collection: [`postman/DeChimex.postman_collection.json`](postman/DeChimex.postman_collection.json)

> The committed `database/migrate.sql` script was **removed** — it was stale (it
> referenced a migration id that is not in the current EF migration set). Generate a
> release script on demand instead:
> `dotnet ef migrations script --idempotent --project src/DeChimex.Persistence --startup-project src/DeChimex.Host`
> In most environments startup auto-migration (`DatabaseSeeder.SeedAsync`) handles it.

---

## Key business rules enforced

- **Orders are never blocked by stock.** Out-of-stock products can still be ordered; shortages raise a domain event and flag the order **AwaitingProcurement**.
- **Inventory is deducted only after successful payment** (gateway verification, webhook, or manual confirmation), and **only for product lines**; services never touch inventory.
- **Every stock movement writes an `InventoryTransaction`** (auditable history).
- **Idempotent payments**, so duplicate webhook/verification calls cannot double-confirm.
- **Soft deletes + audit fields** on business aggregates; **audit log** for critical actions.
- **Role- and permission-based authorization** (Owner, Manager, Sales, Inventory, Designer, Customer. Customer now has `orders.create` for checkout).
- **Inventory Available = OnHand minus Reserved** inlined in queries (EF cannot translate computed property).
- **Order GrandTotal = Items + Services + Delivery + Tax minus Discount, floored at 0**. It is recomputed in SQL where needed because the property is ignored.

Built and verified against **.NET 9.0.316**. Gold zip v029 is ready for owner UAT.
