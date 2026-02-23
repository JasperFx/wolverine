using JasperFx.Blocks;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.SharedMemory;

public class SharedMemorySubscription : SharedMemoryEndpoint, IListener, ISender
{
    private Subscription _subscription;
    private Block<Envelope> _receiver;
    public SharedMemoryTopic Parent { get; }
    public string Name { get; }

    public SharedMemorySubscription(SharedMemoryTopic parent, string name, EndpointRole role) : base(new Uri($"{SharedMemoryTransport.ProtocolName}://{parent.TopicName}/{name}"), role)
    {
        Parent = parent;
        Name = name;
        IsListener = true;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        // Gotta be idempotent here!
        if (_receiver != null) return new ValueTask<IListener>(this);
        
        var topic = SharedMemoryQueueManager.Topics[Parent.TopicName];
        _subscription = topic.Subscriptions[Name];
        _receiver = new Block<Envelope>((e, _) =>
        {
            if (Receiver != null)
            {
                return Receiver.ReceivedAsync(this, e).AsTask();
            }

            throw new InvalidOperationException("This in memory subscription has not been initialized");
        });
        
        _subscription.AddNode(_receiver);
        
        Receiver = receiver;
        return new ValueTask<IListener>(this);
    }

    public IReceiver? Receiver { get; private set; }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        return this;
    }

    IHandlerPipeline? IChannelCallback.Pipeline => Receiver?.Pipeline;
    public ValueTask CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        // Really for retries
        return _receiver.PostAsync(envelope);
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public Uri Address => Uri;
    public async ValueTask StopAsync()
    {
        _receiver.Complete();
        await _receiver.DisposeAsync();
        _subscription.RemoveNode(_receiver);
    }

    public bool SupportsNativeScheduledSend => false;
    public bool SupportsNativeScheduledCancellation => false;
    public Uri Destination => Uri;
    public Task<bool> PingAsync()
    {
        return Task.FromResult(true);
    }

    public ValueTask SendAsync(Envelope envelope)
    {
        if (_receiver != null)
        {
            envelope.ReplyUri ??= ReplyUri;
            return _receiver.PostAsync(envelope);
        }

        return new ValueTask();
    }
}