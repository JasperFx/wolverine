using System.Collections.Concurrent;
using System.Diagnostics;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests.DestinationIsolation;

/// <summary>
/// GH-5587. Durable recovery has to keep delivering to the destinations that can take messages while others cannot:
/// a listener that is slow to take what is recovered for it, or a destination that is not accepting sends, must hold
/// up neither the other destinations nor the other direction of recovery, however many of them are stuck. And nothing
/// more is claimed for a destination that has not yet taken what it already has.
/// </summary>
/// <remarks>
/// A stuck listener is an inline listener whose handler waits on a gate: for an inline listener, taking a recovered
/// message means running it. A stuck sender is a <see cref="GatedTransport" /> destination whose gate is closed. The
/// available destinations are a durable local queue and an open gated destination. Every timeout here is only a bound
/// on failure.
/// </remarks>
public abstract class DestinationRecoveryIsolationCompliance : IAsyncLifetime
{
    /// <summary>
    /// More stuck destinations than the 20 recovery commands the agents queue runs at once
    /// </summary>
    public const int StuckDestinationCount = 25;

    public const string AvailableQueueName = "isolation-available";
    public const string OpenDestinationName = "isolation-open";

    private static readonly TimeSpan FailureBound = 30.Seconds();

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    protected RecoveryIsolationGates Gates { get; } = new();

    private GatedTransport _transport = null!;

    public IHost Host { get; private set; } = null!;

    /// <summary>
    /// Attach the message store under test
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions opts);

    /// <summary>
    /// Start every scenario from empty storage. The default resets Wolverine's own resources
    /// </summary>
    protected virtual Task ResetStateAsync(IHost host)
    {
        return host.ResetResourceState();
    }

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DestinationRecoveryIsolation";
                opts.Durability.Mode = DurabilityMode.Solo;

                // Recover quickly, and a page at a time, so a destination with more than one message needs more than
                // one pass
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();
                opts.Durability.RecoveryBatchSize = 1;

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<StuckRecoveryMessageHandler>()
                    .IncludeType<AvailableRecoveryMessageHandler>();

                opts.Services.AddSingleton(Gates);

                _transport = opts.Transports.GetOrCreate<GatedTransport>();
                for (var i = 0; i < StuckDestinationCount; i++)
                {
                    // Listeners that take a recovered message by running it
                    var listener = _transport.Endpoints[StuckListenerName(i)];
                    listener.IsListener = true;
                    listener.Mode = EndpointMode.Inline;

                    // Destinations whose sends wait while their gate is closed
                    _transport.Endpoints[StuckSenderName(i)].Mode = EndpointMode.Durable;
                }

                _transport.Endpoints[OpenDestinationName].Mode = EndpointMode.Durable;

                opts.LocalQueue(AvailableQueueName).UseDurableInbox();

                ConfigureStorage(opts);
            }).StartAsync(Token);

        await ResetStateAsync(Host);
    }

    public async ValueTask DisposeAsync()
    {
        // Nothing may still be waiting on a gate when the host stops
        Gates.OpenAll();
        for (var i = 0; i < StuckDestinationCount; i++)
        {
            _transport.Endpoints[StuckSenderName(i)].SendGate.Open();
        }

        await Host.StopAsync();
        Host.Dispose();
    }

    public static string StuckListenerName(int i) => $"isolation-stuck-listener-{i}";
    public static string StuckSenderName(int i) => $"isolation-stuck-sender-{i}";

    protected IMessageStore Store => Host.Services.GetRequiredService<IMessageStore>();

    private static Uri availableQueueUri => new($"local://{AvailableQueueName}");

    [Fact]
    public async Task incoming_recovery_keeps_delivering_to_available_destinations_while_more_listeners_than_the_dispatch_slots_are_stuck()
    {
        for (var i = 0; i < StuckDestinationCount; i++)
        {
            await seedIncomingAsync(GatedTransport.ToUri(StuckListenerName(i)), new StuckRecoveryMessage(Guid.NewGuid()));
        }

        var available = await seedIncomingAsync(availableQueueUri, new AvailableRecoveryMessage(Guid.NewGuid()));
        var open = await seedOutgoingAsync(GatedTransport.ToUri(OpenDestinationName));

        // The stuck listeners really are stuck
        await Gates.For(GatedTransport.ToUri(StuckListenerName(0))).FirstEntered.Task.WaitAsync(FailureBound, Token);

        await Gates.Delivered(available).WaitAsync(FailureBound, Token);
        await waitForAsync(() => _transport.Endpoints[OpenDestinationName].Sent.Contains(open),
            "the outbox message for the open destination should be sent while the listeners are stuck");

        // Every stuck listener gets its message once they can run
        Gates.OpenAll();
        await waitForAsync(() => Gates.HandledStuck == StuckDestinationCount,
            "every stuck listener should run its message once its handler is released");
    }

    [Fact]
    public async Task outgoing_recovery_to_destinations_that_are_not_accepting_sends_holds_up_no_other_recovery()
    {
        var stuck = new List<Guid>();
        for (var i = 0; i < StuckDestinationCount; i++)
        {
            _transport.Endpoints[StuckSenderName(i)].SendGate.Close();
            stuck.Add(await seedOutgoingAsync(GatedTransport.ToUri(StuckSenderName(i))));
        }

        // Recovery is waiting on at least one of them
        await Task.WhenAny(Enumerable.Range(0, StuckDestinationCount)
                .Select(i => _transport.Endpoints[StuckSenderName(i)].SendGate.FirstEntered.Task))
            .WaitAsync(FailureBound, Token);

        // Work that turns up after that is still recovered, in both directions
        var available = await seedIncomingAsync(availableQueueUri, new AvailableRecoveryMessage(Guid.NewGuid()));
        var open = await seedOutgoingAsync(GatedTransport.ToUri(OpenDestinationName));

        await Gates.Delivered(available).WaitAsync(FailureBound, Token);
        await waitForAsync(() => _transport.Endpoints[OpenDestinationName].Sent.Contains(open),
            "the outbox message for the open destination should be sent while the other destinations are not accepting sends");

        for (var i = 0; i < StuckDestinationCount; i++)
        {
            _transport.Endpoints[StuckSenderName(i)].SendGate.Open();
        }

        await waitForAsync(
            () => Enumerable.Range(0, StuckDestinationCount)
                .All(i => _transport.Endpoints[StuckSenderName(i)].Sent.Contains(stuck[i])),
            "every destination should get its outbox message once it accepts sends again");
    }

    [Fact]
    public async Task nothing_more_is_claimed_for_a_listener_until_it_has_taken_what_it_already_has()
    {
        var destination = GatedTransport.ToUri(StuckListenerName(0));
        for (var i = 0; i < 3; i++)
        {
            await seedIncomingAsync(destination, new StuckRecoveryMessage(Guid.NewGuid()));
        }

        var gate = Gates.For(destination);
        await gate.FirstEntered.Task.WaitAsync(FailureBound, Token);

        // Two more recovery passes go by, each proven by a message delivered to the available queue
        for (var pass = 0; pass < 2; pass++)
        {
            var probe = await seedIncomingAsync(availableQueueUri, new AvailableRecoveryMessage(Guid.NewGuid()));
            await Gates.Delivered(probe).WaitAsync(FailureBound, Token);
        }

        gate.Entered.ShouldBe(1, "a sequential listener still running its first recovered message should not be handed another");

        var rows = (await Store.Admin.AllIncomingAsync()).Where(x => x.Destination == destination).ToArray();
        rows.Count(x => x.OwnerId == TransportConstants.AnyNode)
            .ShouldBe(2, "the messages the listener has not taken yet should stay in storage for any node to claim");

        Gates.OpenAll();
        await waitForAsync(() => gate.Entered == 3, "the listener should run the rest once the first is done");
        gate.MaximumInside.ShouldBe(1);
    }

    private async Task<Guid> seedIncomingAsync(Uri destination, object message)
    {
        var serializer = Host.GetRuntime().Options.DefaultSerializer;
        var envelope = new Envelope(message)
        {
            Id = Guid.NewGuid(),
            Destination = destination,
            Status = EnvelopeStatus.Incoming,
            OwnerId = TransportConstants.AnyNode,
            ContentType = serializer.ContentType,
            MessageType = message.GetType().ToMessageTypeName(),
            SentAt = DateTimeOffset.UtcNow
        };

        envelope.Data = serializer.Write(envelope);
        await Store.Inbox.StoreIncomingAsync(envelope);

        return message switch
        {
            AvailableRecoveryMessage m => m.Id,
            StuckRecoveryMessage m => m.Id,
            _ => envelope.Id
        };
    }

    private async Task<Guid> seedOutgoingAsync(Uri destination)
    {
        var serializer = Host.GetRuntime().Options.DefaultSerializer;
        var message = new AvailableRecoveryMessage(Guid.NewGuid());
        var envelope = new Envelope(message)
        {
            Id = Guid.NewGuid(),
            Destination = destination,
            Status = EnvelopeStatus.Outgoing,
            OwnerId = TransportConstants.AnyNode,
            ContentType = serializer.ContentType,
            MessageType = message.GetType().ToMessageTypeName(),
            SentAt = DateTimeOffset.UtcNow
        };

        envelope.Data = serializer.Write(envelope);
        await Store.Outbox.StoreOutgoingAsync(envelope, TransportConstants.AnyNode);

        return envelope.Id;
    }

    private static async Task waitForAsync(Func<bool> condition, string because)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < FailureBound)
        {
            if (condition()) return;
            await Task.Delay(50.Milliseconds(), Token);
        }

        condition().ShouldBeTrue(because);
    }
}

public record StuckRecoveryMessage(Guid Id);

public record AvailableRecoveryMessage(Guid Id);

public class RecoveryIsolationGates
{
    private readonly ConcurrentDictionary<Uri, DestinationGate> _gates = new();
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource> _delivered = new();
    private int _handledStuck;

    public DestinationGate For(Uri destination)
    {
        return _gates.GetOrAdd(destination, _ =>
        {
            var gate = new DestinationGate();
            gate.Close();
            return gate;
        });
    }

    public int HandledStuck => Volatile.Read(ref _handledStuck);

    public void OpenAll()
    {
        foreach (var gate in _gates.Values) gate.Open();
        _opened = true;
    }

    private volatile bool _opened;

    public async Task RunStuckAsync(Uri destination)
    {
        var gate = For(destination);
        if (_opened) gate.Open();

        await gate.PassAsync();
        Interlocked.Increment(ref _handledStuck);
    }

    public Task Delivered(Guid id) => completionFor(id).Task;

    public void MarkDelivered(Guid id) => completionFor(id).TrySetResult();

    private TaskCompletionSource completionFor(Guid id)
    {
        return _delivered.GetOrAdd(id, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
    }
}

public class StuckRecoveryMessageHandler
{
    public static Task Handle(StuckRecoveryMessage message, Envelope envelope, RecoveryIsolationGates gates)
    {
        return gates.RunStuckAsync(envelope.Destination!);
    }
}

public class AvailableRecoveryMessageHandler
{
    public static void Handle(AvailableRecoveryMessage message, RecoveryIsolationGates gates)
    {
        gates.MarkDelivered(message.Id);
    }
}
