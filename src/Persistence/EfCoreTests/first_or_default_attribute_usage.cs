using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SharedPersistenceModels.Items;
using Shouldly;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.SqlServer;
using Wolverine.Tracking;
using Xunit;

namespace EfCoreTests;

// The EF Core half of the [FirstOrDefault] storage agnostic promise. Same handler shape as the Marten,
// Polecat, Fisher and RavenDb suites -- the only difference is that EF Core resolves through a DbContext
// and Set<T>() rather than a document session and Query<T>().
[Collection("sqlserver")]
public class first_or_default_attribute_usage : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(EfAlertDefaultsHandler));

                opts.Services.AddDbContextWithWolverineIntegration<AlertDefaultsDbContext>(o =>
                {
                    o.UseSqlServer(Servers.SqlServerConnectionString);
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString, "first_or_default");
                opts.UseEntityFrameworkCoreTransactions();
                opts.UseEntityFrameworkCoreWolverineManagedMigrations();
                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        using var scope = _host.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AlertDefaultsDbContext>();
        await db.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        db.AlertDefaults.RemoveRange(db.AlertDefaults);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_parameter_is_null_when_nothing_is_stored()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadEfAlertDefaults());

        tracked.Sent.SingleMessage<EfAlertDefaultsRead>()
            .Threshold.ShouldBe(-1);
    }

    [Fact]
    public async Task the_first_row_is_supplied_when_one_exists()
    {
        using (var scope = _host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AlertDefaultsDbContext>();
            db.AlertDefaults.Add(new EfAlertDefaults { Id = Guid.NewGuid(), Threshold = 42 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var tracked = await _host.InvokeMessageAndWaitAsync(new ReadEfAlertDefaults());

        tracked.Sent.SingleMessage<EfAlertDefaultsRead>()
            .Threshold.ShouldBe(42);
    }
}

public class EfAlertDefaults
{
    public Guid Id { get; set; }
    public int Threshold { get; set; }
}

public class AlertDefaultsDbContext : DbContext
{
    public AlertDefaultsDbContext(DbContextOptions<AlertDefaultsDbContext> options) : base(options)
    {
    }

    public DbSet<EfAlertDefaults> AlertDefaults { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.MapWolverineEnvelopeStorage();

        modelBuilder.Entity<EfAlertDefaults>(map =>
        {
            map.ToTable("alert_defaults");
            map.HasKey(x => x.Id);
            map.Property(x => x.Threshold);
        });
    }
}

public record ReadEfAlertDefaults;

public record EfAlertDefaultsRead(int Threshold);

public static class EfAlertDefaultsHandler
{
    public static EfAlertDefaultsRead Handle(ReadEfAlertDefaults command,
        [FirstOrDefault] EfAlertDefaults? defaults)
    {
        return new EfAlertDefaultsRead(defaults?.Threshold ?? -1);
    }

    public static void Handle(EfAlertDefaultsRead msg)
    {
    }
}
