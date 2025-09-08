using JasperFx.Blocks;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.SharedMemory;

public class SharedMemorySubscription : SharedMemoryEndpoint, IListener
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
        throw new NotSupportedException();
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

    public async ValueTask DisposeAsync()
    {
        await _receiver.DisposeAsync();
    }

    public Uri Address => Uri;
    public async ValueTask StopAsync()
    {
        _receiver.Complete();
        await _receiver.DisposeAsync();
        _subscription.RemoveNode(_receiver);
    }
}