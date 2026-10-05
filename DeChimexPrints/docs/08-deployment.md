# 08 · Deployment

## Publish
```bash
dotnet publish src/DeChimex.Host -c Release -o ./publish
```

## A. IIS (Windows)
1. Install the **.NET 9 Hosting Bundle** (includes the ASP.NET Core Module).
2. Create an IIS site pointing to `./publish`; app pool **No Managed Code**.
3. Set env vars via `web.config` `<environmentVariables>` or IIS Configuration Editor
   (`ConnectionStrings__DefaultConnection`, `Jwt__SigningKey`, `ASPNETCORE_ENVIRONMENT=Production`).
4. Bind HTTPS with a certificate; enable HSTS.

## B. Docker
`Dockerfile` (place at repo root):
```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore DeChimex.sln
RUN dotnet publish src/DeChimex.Host -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet","DeChimex.Host.dll"]
```
`docker-compose.yml`:
```yaml
services:
  db:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment: { ACCEPT_EULA: "Y", MSSQL_SA_PASSWORD: "Your_password123" }
    ports: ["1433:1433"]
    volumes: ["mssql:/var/opt/mssql"]
  api:
    build: .
    depends_on: [db]
    ports: ["8080:8080"]
    environment:
      ConnectionStrings__DefaultConnection: "Server=db;Database=DeChimexPrintHub;User Id=sa;Password=Your_password123;TrustServerCertificate=True"
      Jwt__SigningKey: "please-generate-a-64-char-random-secret"
      ASPNETCORE_ENVIRONMENT: "Production"
volumes: { mssql: {} }
```

## C. Azure App Service
1. `az webapp up` or CI deploy the `./publish` output (Linux, .NET 9 stack).
2. Provision **Azure SQL**; set the connection string in *Configuration → Connection strings*.
3. Put secrets in *Configuration → Application settings* or **Key Vault** references.
4. Set the Paystack webhook URL to the App Service HTTPS URL. Enable *Always On*.

## D. Linux VPS (Nginx + Kestrel)
```ini
# /etc/systemd/system/dechimex.service
[Unit]
Description=DE-CHIMEX API
After=network.target
[Service]
WorkingDirectory=/var/www/dechimex
ExecStart=/usr/bin/dotnet /var/www/dechimex/DeChimex.Host.dll
Restart=always
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=ASPNETCORE_URLS=http://localhost:5000
EnvironmentFile=/var/www/dechimex/.env
[Install]
WantedBy=multi-user.target
```
```nginx
# /etc/nginx/sites-available/dechimex
server {
    listen 80;
    server_name api.dechimexprinthub.com;
    location / {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection keep-alive;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
    }
}
```
```bash
sudo systemctl enable --now dechimex
sudo certbot --nginx -d api.dechimexprinthub.com   # TLS via Let's Encrypt
```

## E. SQL Server production
- Managed instance (Azure SQL / RDS) or hardened VM. Least-privilege login for the app.
- `Encrypt=True` with a valid certificate. Automated backups + point-in-time restore.
- Apply the seven EF migrations during release: either let startup auto-migrate
  (`DatabaseSeeder.SeedAsync` → `Database.MigrateAsync()`) in lower environments, or run
  `dotnet ef database update`, or generate an idempotent script on demand with
  `dotnet ef migrations script --idempotent` and hand it to the DBA.
  (The committed `database/migrate.sql` was stale and has been removed — do not look for it.)

## F. CI/CD with GitHub Actions
See [`.github/workflows/ci.yml`](../.github/workflows/ci.yml): restore → build → test → publish
(and optional deploy step to Azure/VPS).
