using IntegrationTests;
using JasperFx.Core;
using JasperFx.Events;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Marten.Events.Aggregation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

// GH-3109: the provider-agnostic [Storage(typeof(IMyStore))] attribute must route a handler to a Marten
// ancillary store exactly like [MartenStore], by resolving the Marten IAncillaryStoreFrameProvider from
// the store marker type. The Marten half of the same coverage the Polecat tests give
// (PolecatTests/AncillaryStores/storage_attribute_routes_to_polecat_store).
//
// GH-3907: extended past "the document landed somewhere" to the two behaviors StorageAttribute's own
// doc comment promises, because downstream consumers (CritterWatch) are adopting [Storage] wholesale
// against Marten ancillary stores:
//   1. the work commits through the TARGETED store's session -- and demonstrably NOT the main store;
//   2. inline-projection side effects relayed by that store flow through the Wolverine outbox.
// Both are asserted for the injected-IDocumentSession handler shape as well as the IMartenOp shape,
// since [Storage] has to supply the ancillary store's outbox-enrolled session either way.
public class storage_attribute_routes_to_marten_store : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "storage_attr_main";
                    m.Events.DatabaseSchemaName = "storage_attr_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<IStorageAttrStore>(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "storage_attr_players";
                        m.Events.DatabaseSchemaName = "storage_attr_players";

                        // An INLINE projection that publishes a Wolverine message from RaiseSideEffects.
                        // EnableSideEffectsOnInlineProjections is the per-store opt-in.
                        m.Events.EnableSideEffectsOnInlineProjections = true;
                        m.Projections.Add(new StorageAttrScoreProjection(), ProjectionLifecycle.Inline);
                    })
                    .IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(StorageAttrPlayerHandler))
                    .IncludeType(typeof(StorageAttrScoredHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task storage_attribute_opens_and_commits_through_the_marten_ancillary_store()
    {
        var message = new StorageAttrPlayerMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        var store = theHost.DocumentStore<IStorageAttrStore>();
        await using var session = store.QuerySession();
        (await session.LoadAsync<StorageAttrPlayer>(message.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();

        // ... and the main store never saw it. Without this the test would still pass if [Storage] were
        // silently writing to both stores.
        var mainStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var mainSession = mainStore.QuerySession();
        (await mainSession.LoadAsync<StorageAttrPlayer>(message.Id, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task storage_attribute_injects_the_ancillary_stores_session()
    {
        // Same assertion, but for a handler that takes IDocumentSession as a parameter rather than
        // returning an IMartenOp -- the shape the Polecat mirror uses, and the shape a store-agnostic
        // consumer writes. [Storage] must resolve the injected session to the ancillary store.
        var message = new StorageAttrInjectedMessage(Guid.NewGuid().ToString());
        await theHost.InvokeMessageAndWaitAsync(message);

        var store = theHost.DocumentStore<IStorageAttrStore>();
        await using var session = store.QuerySession();
        (await session.LoadAsync<StorageAttrPlayer>(message.Id, TestContext.Current.CancellationToken))
            .ShouldNotBeNull();

        var mainStore = theHost.Services.GetRequiredService<IDocumentStore>();
        await using var mainSession = mainStore.QuerySession();
        (await mainSession.LoadAsync<StorageAttrPlayer>(message.Id, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task inline_projection_side_effects_flow_through_the_wolverine_outbox()
    {
        // The session [Storage] hands the handler has to be the ancillary store's OUTBOX-ENROLLED
        // session, not merely a session against the right database. Appending an event through it must
        // drive the inline projection's RaiseSideEffects -> PublishMessage back out through Wolverine.
        var streamId = Guid.NewGuid();

        var tracked = await theHost
            .TrackActivity()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new StorageAttrRecordScore(streamId));

        tracked.Executed.MessagesOf<StorageAttrScored>()
            .ShouldContain(m => m.StreamId == streamId);
    }
}

public interface IStorageAttrStore : IDocumentStore;

public class StorageAttrPlayer
{
    public string Id { get; set; } = null!;
}

public record StorageAttrPlayerMessage(string Id);

public record StorageAttrInjectedMessage(string Id);

public record StorageAttrRecordScore(Guid StreamId);

public record StorageAttrScoreRecorded;

public record StorageAttrScored(Guid StreamId, int Score);

public class StorageAttrScore
{
    public Guid Id { get; set; }
    public int Score { get; set; }

    public void Apply(StorageAttrScoreRecorded _) => Score++;
}

public class StorageAttrScoreProjection : SingleStreamProjection<StorageAttrScore, Guid>
{
    public static StorageAttrScore Create(StorageAttrScoreRecorded _, IEvent metadata) =>
        new() { Id = metadata.StreamId, Score = 1 };

    public override ValueTask RaiseSideEffects(IDocumentOperations operations, IEventSlice<StorageAttrScore> slice)
    {
        if (slice.Snapshot is not null)
        {
            slice.PublishMessage(new StorageAttrScored(slice.Snapshot.Id, slice.Snapshot.Score));
        }

        return ValueTask.CompletedTask;
    }
}

#region sample_generic_storage_attribute_handler_marten
// The provider-agnostic [Storage] attribute -- works for Marten, Polecat, ... -- routes every handler on
// this class to the IStorageAttrStore ancillary store.
[Storage(typeof(IStorageAttrStore))]
public static class StorageAttrPlayerHandler
{
    public static IMartenOp Handle(StorageAttrPlayerMessage message)
    {
        return MartenOps.Store(new StorageAttrPlayer { Id = message.Id });
    }

    public static void Handle(StorageAttrInjectedMessage message, IDocumentSession session)
    {
        session.Store(new StorageAttrPlayer { Id = message.Id });
    }

    public static void Handle(StorageAttrRecordScore message, IDocumentSession session)
    {
        session.Events.StartStream<StorageAttrScore>(message.StreamId, new StorageAttrScoreRecorded());
    }
}

#endregion

public static class StorageAttrScoredHandler
{
    public static void Handle(StorageAttrScored message)
    {
    }
}
