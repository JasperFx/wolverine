using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence.Durability;
using Wolverine.RabbitMQ;
using Wolverine.Tracking;
using Wolverine.Util;
using Xunit;

namespace PersistenceTests.Bugs;

// Regression test for GH-4705, the Marten half of GH-4701.
//
// One message type is delivered to two Rabbit MQ queues, so under MessageIdentity.IdAndDestination the inbox holds
// one row per queue with the SAME envelope id. The handler on the first queue commits a Marten session, and
// FlushOutgoingMessagesOnCommit queues the mark-handled UPDATE into that session's batch. That statement used to be
// hand-written as "... where id = ?" with no received_at predicate, so it also retired the second queue's row while
// the second handler had not run yet. A node that stopped at that point left the second copy Handled; the durability
// agent only recovers Incoming rows, so the copy was lost with no dead letter and no log.
//
// Marten's is the one that mattered most: its store is PostgreSQL, the only provider where EnableInboxPartitioning
// exists, so both GH-4701 failure modes were live on it.
//
// This lives in PersistenceTests rather than MartenTests because it needs a broker: a LOCAL queue fan-out mints a
// distinct envelope id per delivery, so it cannot produce the shared-id inbox rows the defect needs. CIPersistence
// starts rabbitmq alongside postgresql; the Marten shards start postgresql alone.
//
// The two gates force the only interleaving that exposes it: the Marten handler may not commit until the gated
// handler has started, which guarantees the second row is already persisted (the durable receiver stores before it
// executes).

public record MartenMessageForTwoDestinations(Guid Id);

public sealed class MartenTwoDestinationsGates
{
    public TaskCompletionSource GatedHandlerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource ReleaseGatedHandler { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class MartenTwoDestinationsDoc
{
    public Guid Id { get; set; }
}

[StickyHandler(Bug_4705_marten_commit_leaves_sibling_destinations_alone.MartenQueue)]
public sealed class MartenMessageForTwoDestinationsMartenHandler
{
    public async Task Handle(MartenMessageForTwoDestinations message, IDocumentSession session,
        MartenTwoDestinationsGates gates)
    {
        session.Store(new MartenTwoDestinationsDoc { Id = message.Id });

        // Do not let the session commit until the sibling delivery is in the inbox and being handled
        await gates.GatedHandlerStarted.Task.WaitAsync(30.Seconds());
    }
}

[StickyHandler(Bug_4705_marten_commit_leaves_sibling_destinations_alone.GatedQueue)]
public sealed class MartenMessageForTwoDestinationsGatedHandler
{
    public async Task Handle(MartenMessageForTwoDestinations message, MartenTwoDestinationsGates gates)
    {
        gates.GatedHandlerStarted.TrySetResult();
        await gates.ReleaseGatedHandler.Task.WaitAsync(30.Seconds());
    }
}

public class Bug_4705_marten_commit_leaves_sibling_destinations_alone : IAsyncLifetime
{
    public const string MartenQueue = "marten-two-destinations-marten";
    public const string GatedQueue = "marten-two-destinations-gated";
    private const string Exchange = "marten-two-destinations";

    private readonly MartenTwoDestinationsGates _gates = new();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                // Other handlers in this assembly need persistence this host does not register
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<MartenMessageForTwoDestinationsMartenHandler>()
                    .IncludeType<MartenMessageForTwoDestinationsGatedHandler>();

                opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;

                // One logical message, two physical deliveries: the destination has to be part of the inbox identity
                opts.Durability.MessageIdentity = MessageIdentity.IdAndDestination;

                opts.UseRabbitMq().AutoProvision().AutoPurgeOnStartup();

                opts.PublishMessage<MartenMessageForTwoDestinations>()
                    .ToRabbitExchange(Exchange, e =>
                    {
                        e.BindQueue(MartenQueue);
                        e.BindQueue(GatedQueue);
                    })
                    .UseDurableOutbox();

                opts.ListenToRabbitQueue(MartenQueue).Named(MartenQueue).UseDurableInbox();
                opts.ListenToRabbitQueue(GatedQueue).Named(GatedQueue).UseDurableInbox();

                opts.Services.AddSingleton(_gates);

                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "marten_two_destinations";
                }).IntegrateWithWolverine(x => x.MessageStorageSchemaName = "marten_two_destinations_wolverine");

                opts.Policies.AutoApplyTransactions();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();

        await _host.ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        // Never leave a handler parked, or shutdown waits on it
        _gates.GatedHandlerStarted.TrySetResult();
        _gates.ReleaseGatedHandler.TrySetResult();

        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task committing_the_marten_session_leaves_the_sibling_destination_incoming()
    {
        var martenDestination = new Uri($"rabbitmq://queue/{MartenQueue}");
        var gatedDestination = new Uri($"rabbitmq://queue/{GatedQueue}");

        await _host.MessageBus().PublishAsync(new MartenMessageForTwoDestinations(Guid.NewGuid()));

        // The Marten commit is what marks its own row Handled -- the moment the sibling used to be retired too
        var rows = await waitForRowsAsync(x =>
            x.Any(e => e.Destination == martenDestination && e.Status == EnvelopeStatus.Handled));

        rows.Length.ShouldBe(2, "One inbox row per destination under IdAndDestination");
        rows.Select(x => x.Id).Distinct().Count().ShouldBe(1, "Both deliveries carry the same envelope id");

        rows.Single(x => x.Destination == gatedDestination).Status.ShouldBe(EnvelopeStatus.Incoming,
            "The gated handler has not finished, so its row must stay recoverable. Marked Handled here, a node that stops now loses this copy for good.");

        _gates.ReleaseGatedHandler.TrySetResult();

        await waitForRowsAsync(x =>
            x.Any(e => e.Destination == gatedDestination && e.Status == EnvelopeStatus.Handled));
    }

    private async Task<Envelope[]> waitForRowsAsync(Func<Envelope[], bool> condition)
    {
        var messageTypeName = typeof(MartenMessageForTwoDestinations).ToMessageTypeName();
        var storage = _host.GetRuntime().Storage;
        var deadline = DateTimeOffset.UtcNow.Add(30.Seconds());

        while (true)
        {
            var rows = (await storage.Admin.AllIncomingAsync())
                .Where(x => x.MessageType == messageTypeName)
                .ToArray();

            if (condition(rows)) return rows;

            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"The inbox never reached the expected state. Rows: {rows.Select(x => $"{x.Destination} {x.Status}").Join(", ")}");
            }

            await Task.Delay(100.Milliseconds());
        }
    }
}
