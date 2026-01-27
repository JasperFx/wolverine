using System.Collections.Immutable;
using System.Diagnostics;
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
        Forwarder = new Block<Envelope>(forwardAsync);
    }

    public override async ValueTask DisposeAsync()
    {
        await Forwarder.DisposeAsync();
        foreach (var receiver in _receivers)
        {
            await receiver.DisposeAsync();
        }
    }

    public override async Task WaitForCompletionAsync()
    {
        await Forwarder.WaitForCompletionAsync();
        foreach (var receiver in _receivers)
        {
            await receiver.WaitForCompletionAsync();
        }
    }

    public override void Complete()
    {
        Forwarder.Complete();
    }

    public override ValueTask PostAsync(Envelope item)
    {
        return Forwarder.PostAsync(item);
    }

    public override void Post(Envelope item)
    {
        Forwarder.Post(item);
    }

    public override uint Count => Forwarder.Count + (uint)_receivers.Sum(x => x.Count);

    public Block<Envelope> Forwarder { get; }

    private async Task forwardAsync(Envelope env, CancellationToken token)
    {
        if (_receivers.IsEmpty) return;

        if (_receivers.Length == 1)
        {
            await _receivers[0].PostAsync(env);
            return;
        }
        
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