# DE-CHIMEX PRINT HUB: v012 Security Hardening

**Date:** 2026-07-21  
**Baseline:** v011 (clean clone of `Oluwaseun1103/DeChimex_v011`)  
**Branch:** `v012-security-hardening`  
**Phase:** Phase 2 - Security Hardening (complete)

This document records the v012 security-hardening work: re-applying the five v012 fixes onto
the v011 baseline and completing the three remaining items, plus the test-contract alignment
needed to make the integration suite green.

---

## Verification (after all changes)

| Check | Result |
|---|---|
| `dotnet build DeChimex.sln` | **0 warnings, 0 errors** (was 2 warnings - NU1903) |
| Unit tests (`DeChimex.UnitTests`) | **71 / 71 passing** |
| Integration tests (`DeChimex.IntegrationTests`) | **24 / 24 passing** (was **0 / 24** at baseline) |
| SQL injection audit (HIGH-008) | **Clean** - no raw SQL anywhere |

---

## The 8 security items

| ID | Title | Action | Key file(s) |
|---|---|---|---|
| HIGH-001 | AutoMapper NU1903 vulnerability | Removed unused AutoMapper (package + `AddAutoMapper`) | `DeChimex.Application.csproj`, `Application/DependencyInjection.cs` |
| HIGH-002 | Test database setup | Full EF registration swap to InMemory; provider-aware seeder; test route/auth/limiter fixes | `CustomWebApplicationFactory.cs`, `DatabaseSeeder.cs`, test files |
| HIGH-003 | Public config endpoint | New anonymous `GET /api/v1/config` (Paystack **public** key only) | `Controllers/ConfigController.cs` (new) |
| HIGH-004 | Production configuration | `appsettings.Production.json` + `.gitignore` | `appsettings.Production.json` (new), `.gitignore` (new) |
| HIGH-005 | HTTPS enforcement | HSTS (preload/subdomains/365d) + HTTPS redirect in prod | `Program.cs` |
| HIGH-006 | File-upload path traversal | Validate/stay-in-root in `DeleteAsync` | `Storage/LocalFileStorageService.cs` |
| HIGH-007 | Auth rate limiting | `[EnableRateLimiting("auth")]` (10 req/min/IP) | `Controllers/AuthController.cs` |
| HIGH-008 | SQL injection audit | Audited - no raw SQL; EF LINQ only | (no code change) |

---

## Notable findings & decisions

1. **AutoMapper was registered but never used.** The handoff framed HIGH-001 as a
   "migrate AutoMapper → Mapster" task, but v011 has no mapping profiles, no `IMapper`
   injection and no `.Map()`/`ProjectTo` calls - handlers map manually. The correct, lowest-risk
   fix was to **remove** the unused, vulnerable package (clearing NU1903) rather than add a
   replacement mapper that would also go unused.

2. **Integration tests never actually ran at v011.** The dual-provider crash aborted host
   build, masking several latent test defects. Fixing the database setup surfaced them:
   - Tests targeted unversioned routes (`/api/products`) while controllers route at
     `api/v{version}/[controller]` → corrected to `/api/v1/...` (the frontend base is `/api/v1`).
   - The factory seeded a test admin into a throwaway InMemory database the host never saw,
     with a fabricated password hash and no permissions. Replaced with the application's own
     `DatabaseSeeder`, which provisions a fully-permissioned Owner account
     (`owner@dechimexprinthub.com / ChangeMe123!`, from the `Seed` config section).
   - The auth rate limiter (HIGH-007) throttled the many auth calls tests make from one
     loopback IP. The limiter is bypassed **in the test host only** (global + `auth` policy
     set to no-limiters); production rate limiting is untouched.

3. **Three controller/test contract mismatches** were resolved in favour of the (standard,
   sensible) contract the tests encode - all confirmed frontend-safe:
   - `PUT /products/{id}` now returns the **updated ProductDto** (re-queried) instead of a bare message.
   - `DELETE /products/{id}` now returns **204 No Content** (the frontend `http()` helper already
     special-cases `204`).
   - Wrong current password on `change-password` now returns **400** (validation error) instead of
     `401` - the caller is already authenticated, so `401` was semantically wrong. No unit test
     depended on the old behaviour.

4. **HIGH-008 audit method:** searched for `FromSqlRaw/FromSqlInterpolated`,
   `ExecuteSqlRaw/ExecuteSqlInterpolated`, ADO.NET `SqlCommand/ExecuteReader/ExecuteScalar`,
   raw `SELECT/INSERT/UPDATE/DELETE/EXEC` literals, and string-built queries. The only hits were
   MediatR CQRS `*Command` objects (not SQL), an email subject line containing the word "Update",
   and a static EF-generated DDL script (`database/migrate.sql`, since removed as stale —
   it contained no user input either). All runtime data access is
   parameterized EF Core LINQ → **no injection vectors**.

---

## Changed files (vs v011)

**Modified (11):**
- `src/DeChimex.Api/Program.cs`
- `src/DeChimex.Api/Controllers/AuthController.cs`
- `src/DeChimex.Api/Controllers/ProductsController.cs`
- `src/DeChimex.Application/DeChimex.Application.csproj`
- `src/DeChimex.Application/DependencyInjection.cs`
- `src/DeChimex.Application/Features/Auth/Commands/PasswordManagement.cs`
- `src/DeChimex.Infrastructure/Persistence/DatabaseSeeder.cs`
- `src/DeChimex.Infrastructure/Storage/LocalFileStorageService.cs`
- `tests/DeChimex.IntegrationTests/CustomWebApplicationFactory.cs`
- `tests/DeChimex.IntegrationTests/Controllers/AuthControllerTests.cs`
- `tests/DeChimex.IntegrationTests/Controllers/ProductsControllerTests.cs`

**Added (3):**
- `src/DeChimex.Api/Controllers/ConfigController.cs`
- `src/DeChimex.Api/appsettings.Production.json`
- `.gitignore`

A unified diff of all changes is available in `v012_changes.patch` at the repo root.

---

## How to verify

```bash
cd DeChimexPrints
dotnet build DeChimex.sln                       # 0 warnings, 0 errors
dotnet test tests/DeChimex.UnitTests            # 71 passing
dotnet test tests/DeChimex.IntegrationTests     # 24 passing
```

---

## Next (Phase 3 & 4)

- **Phase 3 - Stability & Quality** (12 medium-priority issues)
- **Phase 4 - Performance & Polish** (9 low-priority issues)

The specific issue lists for these phases were not enumerated in the handoff note; they need to
be sourced from the original audit report before work begins.
