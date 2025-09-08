using System.Collections.Immutable;
using JasperFx.Blocks;

namespace Wolverine.Transports.SharedMemory;

public class Subscription : BlockBase<Envelope>
{
    public string Name { get; }
    private readonly SemaphoreSlim _semaphore = new(1);
    private ImmutableArray<IBlock<Envelope>> _receivers = ImmutableArray<IBlock<Envelope>>.Empty;
    private int _slot = 0;

    public Subscription(string name)
    {
        Name = name;
        Sender = new Block<Envelope>(forwardAsync);
    }

    public override async ValueTask DisposeAsync()
    {
        await Sender.DisposeAsync();
        foreach (var receiver in _receivers)
        {
            await receiver.DisposeAsync();
        }
    }

    public override async Task WaitForCompletionAsync()
    {
        await Sender.WaitForCompletionAsync();
        foreach (var receiver in _receivers)
        {
            await receiver.WaitForCompletionAsync();
        }
    }

    public override void Complete()
    {
        Sender.Complete();
    }

    public override ValueTask PostAsync(Envelope item)
    {
        return Sender.PostAsync(item);
    }

    public override void Post(Envelope item)
    {
        Sender.Post(item);
    }

    public override uint Count => Sender.Count + (uint)_receivers.Sum(x => x.Count);

    public Block<Envelope> Sender { get; }

    private async Task forwardAsync(Envelope env, CancellationToken token)
    {
        if (_receivers.IsEmpty) return;
        
        await _semaphore.WaitAsync(token);
        try
        {
            if (_slot >= _receivers.Length - 1)
            {
                _slot = 0;
            }

            await _receivers[_slot].PostAsync(env);
            _slot++;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void AddNode(IBlock<Envelope> block)
    {
        _semaphore.Wait();
        try
        {
            _receivers = _receivers.Add(block);
        }
        finally
        {
            _semaphore.Release();
        }
    }

    public void RemoveNode(IBlock<Envelope> block)
    {
        _semaphore.Wait();
        try
        {
            _receivers = _receivers.Remove(block);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}