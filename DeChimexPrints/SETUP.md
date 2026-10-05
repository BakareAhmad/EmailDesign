# DeChimex Print Hub: Local Development Setup

This guide will help you set up the DeChimex Print Hub backend for local development.

## Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- [SQL Server](https://www.microsoft.com/en-us/sql-server/sql-server-downloads) (LocalDB or full installation)
- [Git](https://git-scm.com/downloads)

## Quick Start

### 1. Clone the Repository

```bash
git clone <repository-url>
cd DeChimexPrints
```

### 2. Configure Database Connection

The application requires a SQL Server database. You have two options:

#### Option A: SQL Server LocalDB (Recommended for Development)

LocalDB is included with Visual Studio or can be installed separately. Update your connection string:

```bash
# Using dotnet user-secrets (recommended)
cd src/DeChimex.Host
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=(localdb)\\mssqllocaldb;Database=DeChimexDb;Trusted_Connection=true;TrustServerCertificate=true;"
```

#### Option B: Full SQL Server Installation

If you have a full SQL Server installation:

```bash
cd src/DeChimex.Host
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:DefaultConnection" "Server=localhost;Database=DeChimexDb;User Id=sa;Password=YourPassword;TrustServerCertificate=true;"
```

### 3. JWT signing key — NOT required

The app no longer needs a JWT signing key. MVC login/register authenticate via an
ASP.NET Core **auth cookie**; the JWT access/refresh token service was removed as part of
the JWT-independence change, so `Program.cs` does not validate `Jwt:SigningKey` and there is
no `Jwt` section in `appsettings.json` / `appsettings.Development.json`.

A `Jwt:SigningKey` is **not** needed to boot or to log in. If a future change re-introduces
JWT, add it via user-secrets or an env var (from `src/DeChimex.Host`, after
`dotnet user-secrets init`); do not commit a real key.

### 4. Configure Paystack (Optional: for Payment Testing)

If you want to test payment functionality:

1. Sign up at [Paystack](https://paystack.com/)
2. Get your test API keys from the dashboard
3. Configure them:

```bash
dotnet user-secrets set "Paystack:PublicKey" "pk_test_your_key_here"
dotnet user-secrets set "Paystack:SecretKey" "sk_test_your_key_here"
```

### 5. Run Database Migrations

The application uses Entity Framework Core migrations:

```bash
# Install EF Core tools (if not already installed)
dotnet tool install --global dotnet-ef

# Apply migrations
cd src/DeChimex.Host
dotnet ef database update
```

### 6. Run the Application

```bash
cd src/DeChimex.Host
dotnet run
```

The API will be available at `https://localhost:5001` (or the port shown in the console).

### 7. Access Swagger UI

Open your browser and navigate to:
```
https://localhost:5001/swagger
```

## Environment Variables (Alternative to User Secrets)

You can also use environment variables instead of user secrets:

### Windows (PowerShell)

```powershell
$env:ConnectionStrings__DefaultConnection = "Server=(localdb)\\mssqllocaldb;Database=DeChimexDb;Trusted_Connection=true;TrustServerCertificate=true;"
dotnet run --project src/DeChimex.Host
```

### Linux/macOS

```bash
export ConnectionStrings__DefaultConnection="Server=(localdb)\\mssqllocaldb;Database=DeChimexDb;Trusted_Connection=true;TrustServerCertificate=true;"
dotnet run --project src/DeChimex.Host
```

## Configuration Hierarchy

The application loads configuration in this order (later sources override earlier ones):

1. `appsettings.json` - Base configuration (committed to Git)
2. `appsettings.Development.json` - Development overrides (committed to Git)
3. `appsettings.Production.json` - Production overrides (NOT committed to Git)
4. User Secrets - Local development secrets (NOT committed to Git)
5. Environment Variables - Runtime configuration
6. Command-line Arguments

## Security Best Practices

### Never Commit Secrets

- ❌ Never put real passwords, API keys, or connection strings in `appsettings.json`
- ✅ Use `dotnet user-secrets` for local development
- ✅ Use environment variables for CI/CD and production
- ✅ Use Azure Key Vault or similar for production secrets

### Production Deployment

For production deployment:

1. Create `appsettings.Production.json` with non-sensitive production settings
2. Use environment variables or Azure Key Vault for secrets
3. Never commit `appsettings.Production.json` to Git (already in `.gitignore`)

Example production environment variables:

```bash
ConnectionStrings__DefaultConnection="Server=prod-server;Database=DeChimexDb;User Id=app_user;Password=SecurePassword;"
Paystack__PublicKey="pk_live_xxx"
Paystack__SecretKey="sk_live_xxx"
```

## Troubleshooting

### Database Connection Errors

**Error:** "A network-related or instance-specific error occurred..."

**Solution:**
- Verify SQL Server is running
- Check connection string format
- Ensure firewall allows SQL Server port (default: 1433)
- For LocalDB, ensure it's installed: `sqllocaldb info`

### JWT Config Errors (no longer expected)

The JWT signing key is no longer used (MVC auth is cookie-based, and the JWT token service was
removed). If you see a startup error referencing `Jwt:SigningKey` or `JwtOptions`, you are
running a stale build or an old snapshot — rebuild/restore the current source, which has no
`Jwt` section in the appsettings files and no JWT validation in `Program.cs`.

### Migration Errors

**Error:** "dotnet-ef: command not found"

**Solution:**
```bash
dotnet tool install --global dotnet-ef
```

**Error:** "Unable to create a 'DbContext' of type..."

**Solution:**
- Ensure you're in the `src/DeChimex.Host` directory
- Verify the project builds successfully: `dotnet build`

## Running Tests

```bash
# Run all tests
dotnet test

# Run unit tests only
dotnet test tests/DeChimex.UnitTests

# Run integration tests only
dotnet test tests/DeChimex.IntegrationTests

# Run with coverage report
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
```

## API Documentation

Once the application is running, access the interactive API documentation:

- **Swagger UI:** `https://localhost:5001/swagger`
- **OpenAPI JSON:** `https://localhost:5001/swagger/v1/swagger.json`

## Support

For issues or questions:

1. Check the [README.md](README.md) for project overview
2. Review the [CHANGELOG.md](CHANGELOG.md) for recent changes
3. Check the [Production Readiness Checklist](PRODUCTION_CHECKLIST.md) for deployment guidance

## Next Steps

After successful setup:

1. Explore the API using Swagger UI
2. Run the test suite to verify everything works
3. Review the architecture documentation
4. Start developing features!

Happy coding! 🚀
