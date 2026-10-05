# 07 · Configuration (JWT, CORS, Serilog, HTTPS, Secrets)

All configuration uses the **Options pattern** with startup validation. **No secrets are hardcoded.**
Sections: `Jwt`, `App`, `Brevo`, `Paystack`, `Storage`, `Cors`, `Seed`, `Serilog`, `ConnectionStrings`.

## Complete `appsettings.json` template
See [`src/DeChimex.Host/appsettings.json`](../src/DeChimex.Host/appsettings.json). Key sections:

```jsonc
{
  "ConnectionStrings": { "DefaultConnection": "Server=...;Database=DeChimexPrintHub;..." },
  "Jwt": { "Issuer":"https://api...","Audience":"https://...","SigningKey":"<>=32 chars>","AccessTokenMinutes":15,"RefreshTokenDays":7 },
  "App": { "Name":"DE-CHIMEX PRINT HUB","FrontendBaseUrl":"...","ApiBaseUrl":"...","SupportEmail":"...","DefaultCurrency":"NGN" },
  "Brevo": { "ApiKey":"","SenderEmail":"...","Enabled":false },
  "Paystack": { "SecretKey":"","PublicKey":"","Currency":"NGN" },
  "Storage": { "Provider":"Local","LocalRootPath":"wwwroot/uploads","MaxFileSizeBytes":10485760 },
  "Cors": { "AllowedOrigins": ["https://dechimexprinthub.com","http://localhost:5173"] },
  "Seed": { "OwnerEmail":"owner@dechimexprinthub.com","OwnerPassword":"ChangeMe123!" }
}
```

## `.env` example (for Docker / hosting env vars)
```env
ConnectionStrings__DefaultConnection=Server=db;Database=DeChimexPrintHub;User Id=app;Password=***;TrustServerCertificate=True
Jwt__SigningKey=please-generate-a-64-char-random-string-and-keep-it-secret
Jwt__Issuer=https://api.dechimexprinthub.com
Jwt__Audience=https://dechimexprinthub.com
App__FrontendBaseUrl=https://dechimexprinthub.com
App__ApiBaseUrl=https://api.dechimexprinthub.com
Brevo__ApiKey=xkeysib-***
Brevo__SenderEmail=no-reply@dechimexprinthub.com
Brevo__Enabled=true
Paystack__SecretKey=sk_live_***
Paystack__PublicKey=pk_live_***
Cors__AllowedOrigins__0=https://dechimexprinthub.com
Seed__OwnerEmail=owner@dechimexprinthub.com
Seed__OwnerPassword=change-me-now
ASPNETCORE_ENVIRONMENT=Production
```

## Secret management
- **Local dev:** `dotnet user-secrets set "ConnectionStrings:DefaultConnection" "..."` (User Secrets, never committed). A JWT signing key is **not** required — MVC auth uses a cookie, not JWT.
- **Docker/VPS:** environment variables (`__` = section separator).
- **Azure:** App Service *Configuration* settings or **Key Vault** references.
- The repo excludes `.git/config`, `.netrc`, and credential files from snapshots. Never commit real keys.

## JWT
- HMAC-SHA256, signing key **≥ 32 chars** (validated on startup, `ValidateOnStart`).
- Access token 15 min; refresh token 7 days, **rotated** on use, only the **hash** stored.
- Claims: `sub`, `email`, `name`, roles (`ClaimTypes.Role`), permissions (`permission`).
- Clock skew 30 s; `RequireHttpsMetadata = true`.

## CORS
Configured from `Cors:AllowedOrigins`. With origins set, credentials are allowed and
`X-Correlation-Id` is exposed. Empty list falls back to permissive (dev only). Set explicit
origins in production.

## Serilog
Configured from the `Serilog` section: Console + rolling daily File sink (`logs/dechimex-.log`,
31-day retention), enriched with `CorrelationId`, `MachineName`, `EnvironmentName`. Request logging
via `UseSerilogRequestLogging()`. A `PerformanceBehavior` logs slow (>500 ms) requests.

## HTTPS
`UseHttpsRedirection()` is enabled outside Development. Terminate TLS at the reverse proxy
(Nginx/IIS/Azure) or configure Kestrel with a certificate. Enable HSTS at the proxy in production.
