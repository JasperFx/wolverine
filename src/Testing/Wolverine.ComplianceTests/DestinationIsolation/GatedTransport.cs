using System.Collections.Concurrent;
using JasperFx.Core;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.ComplianceTests.DestinationIsolation;

/// <summary>
/// GH-5587. A broker-free transport whose destinations can be made to stop taking messages: a closed
/// <see cref="DestinationGate" /> holds every send to that destination until it opens, the way a broker that is not
/// accepting sends would. Its listeners have no wire, so their messages only ever arrive through inbox recovery.
/// </summary>
public class GatedTransport : TransportBase<GatedEndpoint>
{
    public const string ProtocolName = "gated";

    public GatedTransport() : base(ProtocolName, "Gated test transport", [])
    {
        Endpoints = new LightweightCache<string, GatedEndpoint>(name => new GatedEndpoint(name));
    }

    public new LightweightCache<string, GatedEndpoint> Endpoints { get; }

    public static Uri ToUri(string endpointName)
    {
        return new Uri($"{ProtocolName}://{endpointName}");
    }

    // Nothing ever replies through these
    public override Endpoint? ReplyEndpoint() => null;

    public override bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    protected override IEnumerable<GatedEndpoint> endpoints() => Endpoints;

    protected override GatedEndpoint findEndpointByUri(Uri uri) => Endpoints[uri.Host];
}

public class GatedEndpoint : Endpoint
{
    public GatedEndpoint(string name) : base(GatedTransport.ToUri(name), EndpointRole.Application)
    {
        EndpointName = name;
        BrokerRole = "gated";
    }

    /// <summary>
    /// Holds every send to this destination while it is closed. Open to begin with
    /// </summary>
    public DestinationGate SendGate { get; } = new();

    /// <summary>
    /// The ids of the envelopes this destination accepted, in the order it accepted them
    /// </summary>
    public ConcurrentQueue<Guid> Sent { get; } = new();

    protected override bool supportsMode(EndpointMode mode) => true;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return ValueTask.FromResult<IListener>(new GatedListener(Uri));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime) => new GatedSender(this);
}

internal class GatedSender : ISender
{
    private readonly GatedEndpoint _endpoint;

    public GatedSender(GatedEndpoint endpoint)
    {
        _endpoint = endpoint;
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => _endpoint.Uri;
    public Task<bool> PingAsync() => Task.FromResult(true);

    public async ValueTask SendAsync(Envelope envelope)
    {
        await _endpoint.SendGate.PassAsync();
        _endpoint.Sent.Enqueue(envelope.Id);
    }
}

/// <summary>
/// There is no wire. Envelopes reach the endpoint's receiver only through inbox recovery
/// </summary>
internal class GatedListener : IListener
{
    public GatedListener(Uri address)
    {
        Address = address;
    }

    public Uri Address { get; }
    public IHandlerPipeline? Pipeline => null;
    public ValueTask CompleteAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;
    public ValueTask StopAsync() => ValueTask.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// Holds whatever passes through it while it is closed, and counts how much was ever inside at once
/// </summary>
public class DestinationGate
{
    private TaskCompletionSource _open = opened();
    private int _inside;
    private int _maximumInside;
    private int _entered;

    private static TaskCompletionSource opened()
    {
        var source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        source.SetResult();
        return source;
    }

    /// <summary>
    /// How many have entered the gate so far, whether or not they have passed
    /// </summary>
    public int Entered => Volatile.Read(ref _entered);

    /// <summary>
    /// The most that were ever inside the gate at the same time
    /// </summary>
    public int MaximumInside => Volatile.Read(ref _maximumInside);

    /// <summary>
    /// Completes when the first one enters the gate
    /// </summary>
    public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Close()
    {
        if (_open.Task.IsCompleted)
        {
            _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    public void Open()
    {
        _open.TrySetResult();
    }

    public async Task PassAsync()
    {
        var inside = Interlocked.Increment(ref _inside);
        int maximum;
        while (inside > (maximum = Volatile.Read(ref _maximumInside)))
        {
            if (Interlocked.CompareExchange(ref _maximumInside, inside, maximum) == maximum) break;
        }

        Interlocked.Increment(ref _entered);
        FirstEntered.TrySetResult();

        try
        {
            await _open.Task;
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }
}
