using IntegrationTests;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;

namespace MartenTests.AncillaryStores;

/// <summary>
///     GH-5142. Stream identity is configured per store, so a string-keyed ancillary store beside a Guid-keyed
///     default store is legal. <c>[StreamState]</c> / <c>[StreamEvents]</c> used to ask the DEFAULT store
///     whether streams are keyed by Guid or string, so on a chain routed to the string-keyed ancillary store
///     they went looking for a Guid identity on a command that carries a string key, and the chain could not
///     be built. The same question decides <c>IEvent.StreamId</c> vs <c>IEvent.StreamKey</c> for an
///     <c>IEvent&lt;T&gt;</c> input in the aggregate handler workflow. Same fix shape as GH-4439 and GH-4441.
/// </summary>
public class stream_identity_on_an_ancillary_store_5142 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;

                // Guid-keyed streams, the default
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "stream_identity_main";
                }).IntegrateWithWolverine();

                // String-keyed streams on the store the handler actually reads
                opts.Services.AddMartenStore<IKeyedLedgerStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "stream_identity_ledgers";
                    m.Events.StreamIdentity = StreamIdentity.AsString;
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(KeyedLedgerTimelineHandler));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task stream_reads_on_a_string_keyed_ancillary_store_resolve_a_string_key()
    {
        var key = "ledger-" + Guid.NewGuid().ToString("N");

        var store = theHost.DocumentStore<IKeyedLedgerStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<KeyedLedger>(key, new KeyedLedgerOpened(), new KeyedLedgerCredited(5m));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var timeline = await theHost.MessageBus()
            .InvokeAsync<KeyedLedgerTimeline>(new FindKeyedLedgerTimeline(key), TestContext.Current.CancellationToken);

        timeline.Version.ShouldBe(2);
        timeline.EventCount.ShouldBe(2);
    }
}

public interface IKeyedLedgerStore : IDocumentStore;

public class KeyedLedger
{
    public string Id { get; set; } = string.Empty;
}

public record KeyedLedgerOpened;

public record KeyedLedgerCredited(decimal Amount);

public record FindKeyedLedgerTimeline(string Id);

public record KeyedLedgerTimeline(long Version, int EventCount);

[MartenStore(typeof(IKeyedLedgerStore))]
public static class KeyedLedgerTimelineHandler
{
    public static KeyedLedgerTimeline Handle(
        FindKeyedLedgerTimeline query,
        [StreamState] StreamState state,
        [StreamEvents] IReadOnlyList<IEvent> events)
    {
        return new KeyedLedgerTimeline(state.Version, events.Count);
    }
}
