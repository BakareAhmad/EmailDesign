using DeChimex.Persistence.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace DeChimex.Persistence;

/// <summary>
/// Design-time factory used by the EF Core CLI (dotnet ef migrations add / database update)
/// so migrations can be created without booting the full API host (and without running the
/// startup seeder). The connection string is only used to configure the provider; no connection
/// is opened while generating a migration.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<DeChimexDbContext>
{
    public DeChimexDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=localhost;Database=DeChimexDb;Trusted_Connection=true;TrustServerCertificate=true;";

        var optionsBuilder = new DbContextOptionsBuilder<DeChimexDbContext>();
        optionsBuilder.UseSqlServer(connectionString,
            sql => sql.MigrationsAssembly(typeof(DeChimexDbContext).Assembly.FullName));

        return new DeChimexDbContext(optionsBuilder.Options);
    }
}
