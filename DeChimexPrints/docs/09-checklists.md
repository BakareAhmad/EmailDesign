# 09 · Production Checklists

## Security checklist
- [ ] (No JWT signing key needed — MVC auth uses a cookie; no `Jwt` config or key required.)
- [ ] Seeded Owner password changed; strong password policy enforced (validators).
- [ ] HTTPS enforced end-to-end; HSTS enabled at the proxy.
- [ ] `Cors:AllowedOrigins` restricted to real frontend origins (no wildcard in prod).
- [ ] Rate limiting enabled (global 100/min per IP; `auth` policy 10/min).
- [ ] Security headers present (X-Content-Type-Options, X-Frame-Options, Referrer-Policy, Permissions-Policy).
- [ ] Passwords hashed with PBKDF2 (100k iterations, per-password salt); refresh tokens stored hashed.
- [ ] Account lockout after 5 failed logins; token rotation + reuse detection active.
- [ ] Paystack webhook signature verified; no trust of unsigned callbacks.
- [ ] SQL least-privilege login; parameterised queries via EF (no string concatenation).
- [ ] Overposting prevented (DTOs/records as input, not entities); mass-assignment not possible.
- [ ] Stack traces never returned; generic 500 messages; full detail only in logs.

## Performance checklist
- [ ] Read side uses `AsNoTracking` projections (query services), with no aggregate hydration for lists.
- [ ] Indexes on FKs, unique keys, status, dates, references (see configurations).
- [ ] Pagination capped (max page size 100).
- [ ] EF `EnableRetryOnFailure` for transient SQL faults.
- [ ] Response caching / `ICacheService` (memory now, Redis-ready) for hot public reads.
- [ ] Slow-request logging (>500 ms) via PerformanceBehavior; investigate outliers.
- [ ] Async/await + CancellationToken throughout.

## Monitoring & logging checklist
- [ ] Serilog console + rolling file (31-day retention); ship to a sink (Seq/ELK/App Insights) in prod.
- [ ] Correlation IDs on every request (`X-Correlation-Id`) and in logs.
- [ ] Health endpoints wired to the orchestrator: `/health/live`, `/health/ready` (DB check).
- [ ] Alerting on 5xx rate, latency, DB availability, low-stock/procurement notifications.

## Backup & disaster recovery
- [ ] Automated DB backups (full daily + differential/log) with tested restores.
- [ ] Point-in-time restore enabled (managed SQL).
- [ ] Uploaded files backed up (or move Storage provider to Cloudinary/Azure Blob with redundancy).
- [ ] Documented RPO/RTO; runbook for restore; migrations reversible or forward-fixed.

## Scaling recommendations
- **Vertical:** increase API/DB CPU & memory; raise SQL tier/DTUs.
- **Horizontal:** the API is stateless. Run multiple instances behind a load balancer.
  - Move `ICacheService` to **Redis** (distributed). The interface is already Redis-ready.
  - Move `IEmailQueue` / `IBackgroundJobScheduler` to **Hangfire/Service Bus**; the abstractions are already in place.
  - Read replicas for reporting/dashboard queries.
  - Design already anticipates **multi-branch** (add a BranchId dimension to inventory/orders).

## Deployment readiness checklist
- [ ] Environment configuration set (all sections, correct `ASPNETCORE_ENVIRONMENT`).
- [ ] Secrets in Key Vault / env vars; none in source.
- [ ] SSL/TLS valid and enforced.
- [ ] Database migrated (`dotnet ef database update`, a generated idempotent script, or startup auto-migrate) and seeded.
- [ ] Logging shipping + retention verified.
- [ ] Monitoring + health checks green.
- [ ] Rate limiting + CORS + security headers verified.
- [ ] Backup strategy live; rollback plan documented (redeploy previous image + reverse/patch migration).
- [ ] Smoke tests pass (register/login, list products, create order, initialize payment, webhook).
- [ ] Production verification: Owner login, dashboard KPIs load, a full paid-order flow succeeds.
