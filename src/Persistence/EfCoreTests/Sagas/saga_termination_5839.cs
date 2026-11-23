using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.EntityFrameworkCore;
using Wolverine.SqlServer;

namespace EfCoreTests.Sagas;

public class TerminableSagaDbContext : DbContext
{
    public TerminableSagaDbContext(DbContextOptions<TerminableSagaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TerminableSaga>(map =>
        {
            map.ToTable("TerminableSaga", "saga_termination");
            map.HasKey(x => x.Id);
            map.Property(x => x.Touches);
        });

        modelBuilder.Entity<RetainedTerminableSaga>(map =>
        {
            map.ToTable("RetainedTerminableSaga", "saga_termination");
            map.HasKey(x => x.Id);
            map.Property(x => x.Touches);
        });
    }
}

// GH-5839: completing a saga from outside its handlers, as EF Core mapped entities
public class saga_termination_5839 : SagaTerminationCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_termination_ef");

        opts.Services.AddDbContextWithWolverineIntegration<TerminableSagaDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString));

        opts.UseEntityFrameworkCoreTransactions();
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Services.AddResourceSetupOnStartup();
    }
}
