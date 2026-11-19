using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests.Sagas;

public class HistorySagaDbContext : DbContext
{
    public HistorySagaDbContext(DbContextOptions<HistorySagaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<HistorySaga>(map =>
        {
            map.ToTable("HistorySaga", "saga_history");
            map.HasKey(x => x.Id);
            map.Property(x => x.Advances);
        });

        modelBuilder.Entity<RetainedHistorySaga>(map =>
        {
            map.ToTable("RetainedHistorySaga", "saga_history");
            map.HasKey(x => x.Id);
        });
    }
}

// GH-5665: the message history of sagas kept as EF Core entities
public class saga_history_5665 : SagaHistoryCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_history_ef");

        opts.Services.AddDbContextWithWolverineIntegration<HistorySagaDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString));

        opts.UseEntityFrameworkCoreTransactions();
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Services.AddResourceSetupOnStartup();
    }
}
