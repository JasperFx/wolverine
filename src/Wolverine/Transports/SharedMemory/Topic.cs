using JasperFx.Blocks;
using JasperFx.Core;

namespace Wolverine.Transports.SharedMemory;

public class Topic : BlockBase<Envelope>
{
    private readonly Cache<string, Subscription> _subscriptions = new(name => new Subscription(name));
    private readonly object _locker = new();
    
    public string Name { get; }

    public Topic(string name)
    {
        Name = name;
    }
    
    public override async ValueTask DisposeAsync()
    {
        await _subscriptions.MaybeDisposeAllAsync();
    }

    public Cache<string, Subscription> Subscriptions => _subscriptions;

    public override Task WaitForCompletionAsync()
    {
        return Task.WhenAll(_subscriptions.Select(x => x.WaitForCompletionAsync()));
    }

    public override void Complete()
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Complete();
        }
    }

    public override uint Count => (uint)_subscriptions.Sum(x => x.Count);

    public override async ValueTask PostAsync(Envelope item)
    {
        foreach (var subscription in _subscriptions)
        {
            await subscription.PostAsync(item);
        }
    }

    public override void Post(Envelope item)
    {
        foreach (var subscription in _subscriptions)
        {
            subscription.Post(item);
        }
    }
}