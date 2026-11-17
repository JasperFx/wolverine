using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests.Sagas;

public class IdleSagaDbContext : DbContext
{
    public IdleSagaDbContext(DbContextOptions<IdleSagaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<IdleDiagnosticsSaga>(map =>
        {
            map.ToTable("IdleDiagnosticsSaga", "saga_idle");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
            map.Property(x => x.Touches);

            // The index the EF Core saga docs recommend for idle saga queries
            map.HasIndex(x => x.LastModified);
        });
    }
}

// GH-5558: idle saga diagnostics over an EF Core mapped saga
public class saga_idle_diagnostics_5558 : SagaIdleDiagnosticsCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_idle_ef");

        opts.Services.AddDbContextWithWolverineIntegration<IdleSagaDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString));

        opts.UseEntityFrameworkCoreTransactions();
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Services.AddResourceSetupOnStartup();
    }
}
