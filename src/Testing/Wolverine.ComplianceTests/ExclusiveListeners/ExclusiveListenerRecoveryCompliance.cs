using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests.ExclusiveListeners;

/// <summary>
/// GH-3590. Durable listeners that are only ever active on ONE node — <see cref="ListenerScope.Exclusive"/>
/// and <see cref="ListenerScope.PinnedToLeader"/> — can not depend on the per-database durability agent to
/// recover dormant (<c>owner_id = 0</c>) inbox rows, because that agent is distributed per database and
/// routinely lands on a different node than the listener. Recovery is instead owned by the listening node
/// itself through <see cref="ListenerInboxRecovery"/>.
///
/// A provider opts into this suite by supplying nothing more than its storage bootstrapping, so every current
/// and future <see cref="IMessageStore"/> reuses the same harness. The listening endpoint is a broker-free
/// durable transport (<see cref="SingleNodeListenerTransport"/>) so no message broker is needed.
/// </summary>
public abstract class ExclusiveListenerRecoveryCompliance : IAsyncLifetime
{
    /// <summary>
    /// The listener under test in the end to end scenarios — actually started, so it drives its own recovery.
    /// </summary>
    public const string ExclusiveEndpointName = "exclusive-recovery";

    public const string LeaderPinnedEndpointName = "leader-pinned-recovery";

    /// <summary>
    /// An endpoint that carries <see cref="ListenerScope.Exclusive"/> but is deliberately NOT started, standing
    /// in for "the exclusive listener lives on some other node". This is how a single-host test can observe what
    /// the durability agent does — or rather does not do — with dormant rows it must not claim.
    /// </summary>
    public const string DormantEndpointName = "dormant-exclusive-recovery";

    private readonly List<IHost> _hosts = new();

    /// <summary>
    /// The only thing a provider has to supply: attach its message store to the options.
    /// </summary>
    protected abstract void ConfigureStorage(WolverineOptions options);

    /// <summary>
    /// Some providers need extra teardown between runs. The default wipes all Wolverine state through the
    /// standard resource model.
    /// </summary>
    protected virtual Task ResetStateAsync(IHost host)
    {
        return host.ResetResourceState();
    }

    public virtual Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts.ToArray())
        {
            try
            {
                await host.StopAsync();
                host.Dispose();
            }
            catch (Exception)
            {
                // Nothing useful to do about a host that won't shut down cleanly during teardown
            }
        }

        _hosts.Clear();
    }

    protected async Task<IHost> startHostAsync(DurabilityMode mode)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ExclusiveListenerRecovery";
                opts.Durability.Mode = mode;

                // Keep the polling tight so the tests don't have to wait out the 5 second default
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();
                opts.Durability.NodeReassignmentPollingTime = 1.Seconds();
                opts.Durability.HealthCheckPollingTime = 1.Seconds();

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<RecoveredMessageHandler>();

                opts.ListenToSingleNodeEndpoint(ExclusiveEndpointName, ListenerScope.Exclusive);
                opts.ListenToSingleNodeEndpoint(LeaderPinnedEndpointName, ListenerScope.PinnedToLeader);

                // Registered, exclusive, but never listening on this node
                opts.ListenToSingleNodeEndpoint(DormantEndpointName, ListenerScope.Exclusive).IsListener = false;

                ConfigureStorage(opts);
            })
            .StartAsync();

        _hosts.Add(host);

        // Every test method seeds its own dormant rows, so start each one from a clean inbox
        await ResetStateAsync(host);

        return host;
    }

    protected static Uri UriFor(string endpointName)
    {
        return SingleNodeListenerTransport.ToUri(endpointName);
    }

    /// <summary>
    /// Seed dormant inbox rows exactly the way an ungraceful shutdown leaves them: status Incoming, owner_id
    /// back at <see cref="TransportConstants.AnyNode"/>, received_at pointed at the single node listener.
    /// </summary>
    protected static async Task<Envelope[]> seedDormantMessagesAsync(IMessageStore store, IWolverineRuntime runtime,
        Uri destination, int count)
    {
        var serializer = runtime.Options.DefaultSerializer;

        var envelopes = Enumerable.Range(0, count).Select(i =>
        {
            // Deliberately the same Guid on both, so the tests can correlate what was seeded in the inbox with
            // what the handler actually received.
            var id = Guid.NewGuid();
            var envelope = new Envelope(new RecoveredMessage(id, i))
            {
                Id = id,
                Destination = destination,
                Status = EnvelopeStatus.Incoming,
                OwnerId = TransportConstants.AnyNode,
                ContentType = serializer.ContentType,
                MessageType = typeof(RecoveredMessage).ToMessageTypeName(),
                SentAt = DateTimeOffset.UtcNow
            };

            envelope.Data = serializer.Write(envelope);

            return envelope;
        }).ToArray();

        await store.Inbox.StoreIncomingAsync(envelopes);

        return envelopes;
    }

    protected static async Task<bool> waitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(100.Milliseconds());
        }

        return condition();
    }

    #region Store primitive compliance

    [Fact]
    public async Task recover_dormant_messages_for_a_single_node_listener()
    {
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();
        var store = host.Services.GetRequiredService<IMessageStore>();
        var destination = UriFor(DormantEndpointName);

        var circuit = new RecordingListenerCircuit(runtime.Endpoints.EndpointFor(destination)!);
        var seeded = await seedDormantMessagesAsync(store, runtime, destination, 5);

        var recovery = new ListenerInboxRecovery(runtime, circuit, NullLogger.Instance);
        var count = await recovery.RecoverAsync();

        count.ShouldBe(seeded.Length);
        circuit.Enqueued.Select(x => x.Id).OrderBy(x => x)
            .ShouldBe(seeded.Select(x => x.Id).OrderBy(x => x));

        // Every recovered envelope knows which store it came from -- GH-2318
        foreach (var envelope in circuit.Enqueued)
        {
            envelope.Store.ShouldNotBeNull();
        }

        // Reassigned to this node, so a second sweep finds nothing left to claim
        (await recovery.RecoverAsync()).ShouldBe(0);
    }

    [Fact]
    public async Task a_latched_circuit_recovers_nothing()
    {
        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();
        var store = host.Services.GetRequiredService<IMessageStore>();
        var destination = UriFor(DormantEndpointName);

        var circuit = new RecordingListenerCircuit(runtime.Endpoints.EndpointFor(destination)!)
        {
            Status = ListeningStatus.Stopped
        };

        await seedDormantMessagesAsync(store, runtime, destination, 5);

        var recovery = new ListenerInboxRecovery(runtime, circuit, NullLogger.Instance);

        (await recovery.RecoverAsync()).ShouldBe(0);
        circuit.Enqueued.ShouldBeEmpty();

        // Still dormant, and available to whichever node picks up the listener later
        (await store.LoadPageOfGloballyOwnedIncomingAsync(destination, 100)).Count.ShouldBe(5);
    }

    #endregion

    #region End to end

    [Fact]
    public async Task exclusive_listener_recovers_its_own_dormant_inbox_end_to_end()
    {
        await recoversEndToEndAsync(ExclusiveEndpointName);
    }

    [Fact]
    public async Task leader_pinned_listener_recovers_its_own_dormant_inbox_end_to_end()
    {
        await recoversEndToEndAsync(LeaderPinnedEndpointName);
    }

    private async Task recoversEndToEndAsync(string endpointName)
    {
        using var tracking = RecoveredMessages.Track();

        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();
        var store = host.Services.GetRequiredService<IMessageStore>();
        var destination = UriFor(endpointName);

        runtime.Endpoints.FindListeningAgent(destination).ShouldNotBeNull(
            $"Expected the {endpointName} listener to be running in Solo mode");

        var seeded = await seedDormantMessagesAsync(store, runtime, destination, 5);
        var expected = seeded.Select(x => x.Id).ToArray();

        var succeeded = await waitForAsync(() => expected.All(tracking.Contains), 30.Seconds());

        succeeded.ShouldBeTrue(
            $"Expected the {endpointName} listener to recover and handle all {seeded.Length} dormant inbox " +
            $"messages, but only saw {tracking.Count}");
    }

    #endregion

    #region Durability agent exclusion

    /// <summary>
    /// With the single node listener deliberately NOT running on this node, the per-database durability agent
    /// must leave the dormant rows exactly where they are — no claim, no error storm — and they must recover
    /// promptly once the listener does activate here.
    /// </summary>
    [Fact]
    public async Task durability_agent_leaves_dormant_rows_for_a_single_node_listener_alone()
    {
        using var tracking = RecoveredMessages.Track();

        var host = await startHostAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();
        var store = host.Services.GetRequiredService<IMessageStore>();
        var destination = UriFor(DormantEndpointName);

        runtime.Endpoints.FindListeningAgent(destination).ShouldBeNull(
            "This endpoint stands in for an exclusive listener that is active on some other node");

        var seeded = await seedDormantMessagesAsync(store, runtime, destination, 5);

        // Give the durability agent several polling passes to misbehave
        await Task.Delay(2.Seconds());

        var stillDormant = await store.LoadPageOfGloballyOwnedIncomingAsync(destination, 100);
        stillDormant.Count.ShouldBe(seeded.Length,
            "The durability agent must never claim inbox rows for a listener that only runs on one node");

        tracking.Count.ShouldBe(0,
            "Nothing should have been recovered on a node that is not hosting the exclusive listener");

        // ... and the moment the listener does activate here, they are recovered
        await runtime.Endpoints.StartListenerAsync(runtime.Endpoints.EndpointFor(destination)!,
            CancellationToken.None);

        var expected = seeded.Select(x => x.Id).ToArray();
        var succeeded = await waitForAsync(() => expected.All(tracking.Contains), 30.Seconds());

        succeeded.ShouldBeTrue(
            $"Expected all {seeded.Length} dormant messages to be recovered once the exclusive listener started " +
            $"on this node, but only saw {tracking.Count}");
    }

    #endregion
}

/// <summary>
/// Captures what a real <see cref="Wolverine.Transports.ListeningAgent"/> would have enqueued, so the store
/// primitive tests can assert on recovery without standing up a full listener.
/// </summary>
public class RecordingListenerCircuit : IListenerCircuit
{
    public RecordingListenerCircuit(Endpoint endpoint)
    {
        Endpoint = endpoint;
    }

    public List<Envelope> Enqueued { get; } = new();

    public ListeningStatus Status { get; set; } = ListeningStatus.Accepting;

    public Endpoint Endpoint { get; }

    public int QueueCount => Enqueued.Count;

    public ValueTask PauseAsync(TimeSpan pauseTime) => ValueTask.CompletedTask;

    public ValueTask PauseWithDrainAsync(TimeSpan pauseTime) => ValueTask.CompletedTask;

    public ValueTask StartAsync() => ValueTask.CompletedTask;

    public Task EnqueueDirectlyAsync(IEnumerable<Envelope> envelopes)
    {
        Enqueued.AddRange(envelopes);
        return Task.CompletedTask;
    }
}

public record RecoveredMessage(Guid Id, int Sequence);

public class RecoveredMessageHandler
{
    public static void Handle(RecoveredMessage message)
    {
        RecoveredMessages.Record(message);
    }
}

/// <summary>
/// Static handler state is process wide and these suites run serially per assembly, so the tracking session
/// is scoped and disposed rather than reset ad hoc.
/// </summary>
public static class RecoveredMessages
{
    private static readonly object _locker = new();
    private static RecoveredMessageTracking? _current;

    public static RecoveredMessageTracking Track()
    {
        lock (_locker)
        {
            _current = new RecoveredMessageTracking();
            return _current;
        }
    }

    internal static void Record(RecoveredMessage message)
    {
        lock (_locker)
        {
            _current?.Record(message);
        }
    }

    internal static void Clear(RecoveredMessageTracking tracking)
    {
        lock (_locker)
        {
            if (ReferenceEquals(_current, tracking))
            {
                _current = null;
            }
        }
    }
}

public class RecoveredMessageTracking : IDisposable
{
    private readonly object _locker = new();
    private readonly List<RecoveredMessage> _received = new();

    internal void Record(RecoveredMessage message)
    {
        lock (_locker)
        {
            _received.Add(message);
        }
    }

    public int Count
    {
        get
        {
            lock (_locker)
            {
                return _received.Count;
            }
        }
    }

    public bool Contains(Guid id)
    {
        lock (_locker)
        {
            return _received.Any(x => x.Id == id);
        }
    }

    public void Dispose()
    {
        RecoveredMessages.Clear(this);
    }
}
