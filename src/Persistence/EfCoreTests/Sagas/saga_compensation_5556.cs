using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests.Sagas;

#region sample_mapping_a_compensating_saga_with_ef_core
public class CompensatedSagaDbContext : DbContext
{
    public CompensatedSagaDbContext(DbContextOptions<CompensatedSagaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CompensatedSaga>(map =>
        {
            map.ToTable("CompensatedSaga", "saga_compensation");
            map.HasKey(x => x.Id);

            // The saga's compensation status and log
            map.MapCompensatingSaga();
        });
    }
}
#endregion

// GH-5556: a compensating saga's status and log as an EF Core mapped entity
public class saga_compensation_5556 : SagaCompensationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_compensation_ef");

        opts.Services.AddDbContextWithWolverineIntegration<CompensatedSagaDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString));

        opts.UseEntityFrameworkCoreTransactions();
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Services.AddResourceSetupOnStartup();
    }
}
