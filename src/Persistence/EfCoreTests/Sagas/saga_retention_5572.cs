using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests.Sagas;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence.Sagas;
using Wolverine.SqlServer;

namespace EfCoreTests.Sagas;

public class RetainedSagaDbContext : DbContext
{
    public RetainedSagaDbContext(DbContextOptions<RetainedSagaDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RetainedSaga>(map =>
        {
            map.ToTable("RetainedSaga", "saga_retention");
            map.HasKey(x => x.Id);
            map.Property(x => x.Touches);
        });
    }
}

// GH-5572: retained, completed sagas as an EF Core mapped entity
public class saga_retention_5572 : SagaRetentionCompliance
{
    protected override void ConfigureStorage(WolverineOptions opts)
    {
        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_retention_ef");

        opts.Services.AddDbContextWithWolverineIntegration<RetainedSagaDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString));

        opts.UseEntityFrameworkCoreTransactions();
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Services.AddResourceSetupOnStartup();
    }
}

public class UnmappedCompletedAtDbContext : DbContext
{
    public UnmappedCompletedAtDbContext(DbContextOptions<UnmappedCompletedAtDbContext> options) : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<RetainedSaga>(map =>
        {
            map.ToTable("RetainedSagaUnmapped", "saga_retention");
            map.HasKey(x => x.Id);
            map.Ignore(x => x.CompletedAt);
        });
    }
}

// GH-5572: a DbContext that ignores Saga.CompletedAt cannot retain a completed saga, so the saga is refused rather
// than handled again after it completed
public class saga_retention_without_completed_at_mapped_5572
{
    [Fact]
    public async Task retaining_the_saga_is_refused()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<RetainedSaga>();
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "saga_retention_ef");

                opts.Services.AddDbContextWithWolverineIntegration<UnmappedCompletedAtDbContext>(
                    x => x.UseSqlServer(Servers.SqlServerConnectionString));

                opts.UseEntityFrameworkCoreTransactions();
            }).StartAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<InvalidSagaException>(() =>
            host.MessageBus().InvokeAsync(new StartRetainedSaga(Guid.NewGuid().ToString()),
                TestContext.Current.CancellationToken));
        ex.Message.ShouldContain(nameof(Saga.CompletedAt));
    }
}
