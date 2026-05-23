using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events;
using Marten.Events.Aggregation;
using Marten.Events.Projections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Weasel.Postgresql.Migrations;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.Bugs;

/// <summary>
/// GH-2887: a Marten ancillary store on a SEPARATE physical database, integrated with
/// Wolverine using a per-store envelope schema (IntegrateWithWolverine(x => x.SchemaName = ...)),
/// runs an async multi-stream projection that publishes a Wolverine message via
/// RaiseSideEffects()/slice.PublishMessage().
///
/// The projection-batch outbox write should go to the ancillary store's configured
/// envelope schema ("debtors") on the ancillary database. Today the MartenToWolverineOutbox
/// builds a MessageContext bound to the runtime's DEFAULT (main) message store, so the
/// envelope SQL targets the MAIN store's schema ("public"). On the ancillary database that
/// schema's envelope tables do not exist → 42P01, the projection batch fails, and the
/// side-effect message is never delivered.
///
/// This reproduces on a separate physical database specifically because a shared database
/// masks the bug (the main store's "public.wolverine_*" tables exist there, so the
/// wrong-schema write silently succeeds).
/// </summary>
public class Bug_2887_ancillary_outbox_schema_on_separate_database : IAsyncLifetime
{
    private const string RefsDatabase = "bug2887_refs";
    private IHost _host = null!;
    private string _refsConnectionString = null!;

    public async Task InitializeAsync()
    {
        // The ancillary store lives on a SEPARATE physical database. On it, only the
        // per-store envelope schema ("debtors") will exist — the main store's "public"
        // wolverine_* tables do not.
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            if (!await conn.DatabaseExists(RefsDatabase))
            {
                await new DatabaseSpecification().BuildDatabase(conn, RefsDatabase);
            }
        }

        _refsConnectionString = new NpgsqlConnectionStringBuilder(Servers.PostgresConnectionString)
        {
            Database = RefsDatabase
        }.ConnectionString;

        // Fresh state on the refs database.
        await using (var refsConn = new NpgsqlConnection(_refsConnectionString))
        {
            await refsConn.OpenAsync();
            await refsConn.DropSchemaAsync("debtors");
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Main store on the default database, default ("public") envelope schema.
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // Durable local queues: the side-effect message is persisted to the inbox
                // transactionally inside the projection's (ancillary) session — which is where
                // the wrong-schema write hits the ancillary database. Matches the modular-monolith
                // durability setup in GH-2887.
                opts.Policies.UseDurableLocalQueues();

                // Ancillary store on a SEPARATE physical database with its own envelope schema.
                opts.Services.AddMartenStore<IBug2887DebtorStore>(_ =>
                    {
                        var storeOptions = new StoreOptions();
                        storeOptions.Connection(_refsConnectionString);
                        storeOptions.DatabaseSchemaName = "debtors";
                        storeOptions.Events.DatabaseSchemaName = "debtors";
                        storeOptions.DisableNpgsqlLogging = true;
                        storeOptions.Projections.Add<Bug2887DebtorProjection>(ProjectionLifecycle.Async);
                        return storeOptions;
                    })
                    .IntegrateWithWolverine(x => x.SchemaName = "debtors")
                    .AddAsyncDaemon(DaemonMode.Solo);

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<Bug2887DebtorHandler>();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task projection_side_effect_outbox_write_honours_per_store_schema()
    {
        var streamId = Guid.NewGuid();

        var tracked = await _host
            .TrackActivity()
            .Timeout(60.Seconds())
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<DebtorBalanceUpdated>(_host)
            .ExecuteAndWaitAsync((Func<IMessageContext, Task>)(_ => appendAndDriveDaemonAsync(streamId)));

        tracked.Executed.MessagesOf<DebtorBalanceUpdated>()
            .Where(m => m.StreamId == streamId)
            .ShouldHaveSingleItem();
    }

    private async Task appendAndDriveDaemonAsync(Guid streamId)
    {
        using var scope = _host.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IBug2887DebtorStore>();

        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<Bug2887Debtor>(streamId, new DebtorRegistered());
            await session.SaveChangesAsync();
        }

        // Drive the ancillary store's async daemon so the projection batch (and its
        // side-effect outbox write) actually runs.
        using var daemon = await store.BuildProjectionDaemonAsync();
        await daemon.WaitForNonStaleData(60.Seconds());
    }
}

// ── Marker interface for the ancillary store ──
public interface IBug2887DebtorStore : IDocumentStore;

// ── Domain ──
public record DebtorRegistered;

public class Bug2887Debtor
{
    public Guid Id { get; set; }
    public int Balance { get; set; }
}

// ── Multi-stream projection that publishes a side effect through the outbox ──
public partial class Bug2887DebtorProjection : MultiStreamProjection<Bug2887Debtor, Guid>
{
    public Bug2887DebtorProjection()
    {
        Identity<IEvent>(x => x.StreamId);
    }

    public static Bug2887Debtor Create(DebtorRegistered @event, IEvent metadata) =>
        new() { Id = metadata.StreamId, Balance = 1 };

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<Bug2887Debtor> slice)
    {
        if (slice.Snapshot is not null)
        {
            slice.PublishMessage(new DebtorBalanceUpdated(slice.Snapshot.Id, slice.Snapshot.Balance));
        }

        return ValueTask.CompletedTask;
    }
}

public record DebtorBalanceUpdated(Guid StreamId, int Balance);

public class Bug2887DebtorHandler
{
    public static void Handle(DebtorBalanceUpdated msg) => _ = msg;
}
