using Fisher;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using static Bobcat.Specifications;

namespace Bobcat.CritterStack.ScenarioTests;

// bobcat#536: on a conjoined multi-tenant store, ForTenant(id) arranges and reads as one tenant. Without it the
// Givens appended to the default tenant and ThenAggregate read a stream the tenant's act never wrote. Each test
// runs on Marten and on Fisher through the classes at the bottom.

public abstract class tenants<THost>(THost host) where THost : StoreHost
{
    private CritterStackScenario newScenario()
    {
        Assert.SkipUnless(host.IsAvailable, host.SkipReason);
        return new CritterStackScenario(host.Host);
    }

    [Fact]
    public async Task a_tenants_givens_and_thens_write_and_read_that_tenants_streams()
    {
        var scenario = newScenario();
        var red = scenario.ForTenant("red");
        var blue = scenario.ForTenant("blue");
        var wallet = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await red.GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Ann"), new WalletCredited(wallet, 10m));
            await red.GivenEventsOn<Wallet>(wallet, new WalletCredited(wallet, 5m));

            await red.ThenAggregate<Wallet>(wallet, Specify<Wallet>().With(x => x.Balance, 15m));
            await red.ThenEventsOn<Wallet>(wallet, 2L, new WalletCredited(wallet, 5m));
            (await red.TheAggregate<Wallet>(wallet))!.Owner.ShouldBe("Ann");
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Lines().ShouldContain(x => x.StartsWith("Given ") && x.EndsWith("for tenant \"red\""));
        recording.Lines().Where(x => x.StartsWith("Then ")).ShouldAllBe(x => x.EndsWith("for tenant \"red\""));

        (await EventStores.FetchStreamAsync(scenario.EventStore, wallet, "red")).Count.ShouldBe(3);
        (await EventStores.FetchStreamAsync(scenario.EventStore, wallet, "blue")).Count.ShouldBe(0);
        (await blue.TheAggregate<Wallet>(wallet)).ShouldBeNull();
    }

    [Fact]
    public async Task a_tenants_documents_and_projected_read_models_are_that_tenants()
    {
        var scenario = newScenario();
        var red = scenario.ForTenant("red");
        var blue = scenario.ForTenant("blue");
        var customer = Guid.NewGuid();
        var wallet = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await red.GivenReadModel<Customer>(new Customer { Id = customer, Name = "Ann", City = "Austin" });
            await red.ThenDocument<Customer>(customer, Specify<Customer>().With(x => x.City, "Austin"));
            await blue.ThenNoReadModel<Customer>(customer);
            await blue.ThenNoReadModel<Customer>();

            // An async projection: the read waits on the tenant's database first
            await red.GivenEvents<Wallet>(wallet, new WalletOpened(wallet, "Ann"), new WalletCredited(wallet, 3m));
            await red.ThenReadModel<WalletSummary>(wallet, Specify<WalletSummary>().With(x => x.Credits, 1));
            await red.ThenSingleReadModel<WalletSummary>(Specify<WalletSummary>().With(x => x.Balance, 3m));
            await red.AfterAsyncDaemonsCatchUpAsync();
            await blue.ThenNoReadModel<WalletSummary>(wallet);
        });

        recording.GatheredFailures().ShouldBeNull();
        recording.Lines().ShouldContain("Given the async daemons have caught up for tenant \"red\"");

        (await EventStoreAuthoring.LoadDocumentAsync<Customer>(scenario.EventStore, customer, "red")).ShouldNotBeNull();
        (await EventStoreAuthoring.LoadDocumentAsync<Customer>(scenario.EventStore, customer, "blue")).ShouldBeNull();
    }

    [Fact]
    public void a_tenant_view_shares_the_specification_and_names_its_tenant()
    {
        var scenario = newScenario();

        scenario.TenantId.ShouldBeNull();
        var red = scenario.ForTenant("red");
        red.TenantId.ShouldBe("red");
        red.EventStore.ShouldBeSameAs(scenario.EventStore);
        red.ForTenant("blue").TenantId.ShouldBe("blue");

        // A store view and a tenant view compose, in either order
        var name = scenario.EventStore.Identity.Name;
        red.In(name).TenantId.ShouldBe("red");
        scenario.In(name).ForTenant("red").EventStore.ShouldBeSameAs(scenario.EventStore);
        Should.Throw<ArgumentException>(() => scenario.ForTenant(" "));
    }
}

public class tenants_on_marten(MartenTenantedStoreHost host) : tenants<MartenTenantedStoreHost>(host), IClassFixture<MartenTenantedStoreHost>;

public class tenants_on_fisher(FisherTenantedStoreHost host) : tenants<FisherTenantedStoreHost>(host), IClassFixture<FisherTenantedStoreHost>;

/// <summary>Marten with conjoined tenancy for events and documents, in a schema of its own.</summary>
public sealed class MartenTenantedStoreHost : StoreHost
{
    private static int _hosts;
    private readonly string _schema = $"bobcat_cs_tenants_{Interlocked.Increment(ref _hosts)}";

    public override bool IsAvailable => PostgresEnvironment.IsCi || PostgresEnvironment.IsAvailable;

    public override string SkipReason => PostgresEnvironment.SkipReason;

    protected override IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddMarten(opts =>
            {
                opts.Connection(PostgresEnvironment.ConnectionString);
                opts.DatabaseSchemaName = _schema;
                opts.AutoCreateSchemaObjects = AutoCreate.All;
                opts.DisableNpgsqlLogging = true;

                opts.Events.TenancyStyle = TenancyStyle.Conjoined;
                opts.Policies.AllDocumentsAreMultiTenanted();

                opts.Projections.Snapshot<Wallet>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);
                opts.Schema.For<Customer>();
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        return builder.Build();
    }

    protected override async Task BeforeStartAsync()
    {
        await MartenDdl.Gate.WaitAsync();
        await dropSchemaAsync();
    }

    protected override void AfterStartAttempt() => MartenDdl.Gate.Release();

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        if (IsAvailable) await dropSchemaAsync();
    }

    private async Task dropSchemaAsync()
    {
        await using var connection = new Npgsql.NpgsqlConnection(PostgresEnvironment.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP SCHEMA IF EXISTS {_schema} CASCADE";
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>Fisher with conjoined tenancy for events and documents, on a temp SQLite file.</summary>
public sealed class FisherTenantedStoreHost : StoreHost
{
    private readonly string _file = Path.Combine(Path.GetTempPath(), $"bobcat-critterstack-tenants-{Guid.NewGuid():N}.db");

    protected override IHost Build()
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder();
        builder.Services.AddFisher(opts =>
            {
                opts.Connection($"Data Source={_file}");

                opts.Events.TenancyStyle = TenancyStyle.Conjoined;
                opts.Policies.AllDocumentsAreMultiTenanted();

                opts.Projections.Snapshot<Wallet>(SnapshotLifecycle.Inline);
                opts.Projections.Snapshot<WalletSummary>(SnapshotLifecycle.Async);
                opts.Schema.For<Customer>();
            })
            .ApplyAllDatabaseChangesOnStartup()
            .AddAsyncDaemon(DaemonMode.Solo);

        return builder.Build();
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();

        foreach (var file in new[] { _file, _file + "-wal", _file + "-shm" })
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // A temp file; the OS reclaims it
            }
        }
    }
}
