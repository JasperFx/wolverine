namespace Wolverine.Transports;

public interface IListener : IChannelCallback, IAsyncDisposable
{
    Uri Address { get; }

    /// <summary>
    ///     Stop the receiving of any new messages, but leave any connection
    ///     open for possible calls to Defer/Complete
    /// </summary>
    /// <returns></returns>
    ValueTask StopAsync();
}

public class CompoundListener : IListener
{
    public readonly List<IListener> Inner = new();

    public CompoundListener(Uri address)
    {
        Address = address;
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        throw new NotSupportedException();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var listener in Inner)
        {
            await listener.DisposeAsync();
        }
    }

    public Uri Address { get; }
    public async ValueTask StopAsync()
    {
        var exceptions = new List<Exception>();
        foreach (var listener in Inner)
        {
            try
            {
                await listener.StopAsync();
            }
            catch (Exception e)
            {
                exceptions.Add(e);
            }
        }

        if (exceptions.Any()) throw new AggregateException(exceptions);
    }
}