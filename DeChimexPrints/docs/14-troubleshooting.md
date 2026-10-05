# Troubleshooting Guide

Common issues and their fixes when running or deploying the DE-CHIMEX PRINT HUB backend.

## Startup failures

**"Database connection string is not configured."**
Set `ConnectionStrings:DefaultConnection` via user-secrets, environment variables, or
`appsettings.Development.json`. See `docs/07-configuration.md`.

**"JWT signing key is not configured." / "JWT signing key is too short."** (no longer expected)
The JWT signing key requirement was removed: MVC auth uses a cookie and there is no `Jwt`
section or JWT validation in the current source. If you see this, you are running a stale
build/snapshot — restore the current source (which has no `Jwt` config and no ITokenService).

**Migrations fail / model out of sync**
Run `dotnet ef database update --project src/DeChimex.Persistence --startup-project src/DeChimex.Host`.
If you change EF configurations, add a migration:
`dotnet ef migrations add <Name> --project src/DeChimex.Persistence --startup-project src/DeChimex.Persistence`
(a design-time factory is provided, so no live DB is needed to *create* a migration).

## Database & queries

**Slow list queries**
Ensure the `AddPerformanceIndexes` migration is applied (indexes on Email, Sku, OrderNumber,
Status, CreatedDate and common composites). Check the query plan; add a targeted index if a
new filter pattern emerges.

**Stale cached data after an update**
Category/service/settings reads are cached (10-15 min) and invalidated on write. If you add a
new mutating path, invalidate the relevant `CacheKeys` entry.

## Authentication & authorization

**401 on valid token**
Verify `Jwt:Issuer`/`Jwt:Audience` match the token, the signing key matches, and the token
hasn't expired. Note the test/development host requires HTTPS metadata.

**403 (forbidden) for a staff user**
The user's role lacks the required permission. Assign the permission via Roles & Permissions,
or test as `Owner` (all permissions).

**429 (too many requests) on auth endpoints**
Auth endpoints are rate-limited to 10 req/min/IP (brute-force protection). Slow down retries
or raise the limit for load testing only.

## External services

**Paystack errors / webhook not confirming**
Confirm `Paystack:SecretKey`/`PublicKey` are set for the correct mode (test vs live). The
webhook signature is verified with the secret key; mismatched keys cause rejection. Check
`/health` (Paystack entry) for connectivity.

**Emails not sending (Brevo)**
If `Brevo:Enabled` is true, `Brevo:ApiKey` must be set. Emails are queued on a background task;
failures are logged (search logs for "Failed to dispatch queued email"). Check `/health`
(Brevo entry). Set `Brevo:Enabled=false` to disable in development.

## Health & monitoring

- `GET /health/live`: liveness (no dependencies).
- `GET /health/ready`: readiness (database).
- `GET /health`: detailed JSON report including Paystack & Brevo (external failures are
  reported as *Degraded*, not fatal).

## CORS / frontend

**CORS errors in the browser**
Set `Cors:AllowedOrigins` to the frontend origin(s). In production,
`appsettings.Production.json` locks this to the real domain(s). Ensure the frontend
`API_CONFIG.baseUrl` points at the API's `/api/v1`.

## Where to look

- Structured logs include a **correlation id** (`X-Correlation-Id` header) plus the request
  method/path/user for every error. Search logs by correlation id to trace a request.
- `docs/09-checklists.md` has production readiness checklists (security, performance, backup).
