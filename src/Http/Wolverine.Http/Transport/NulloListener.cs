using Wolverine.Transports;

namespace Wolverine.Http.Transport;

internal class NulloListener : IListener
{
    public NulloListener(Uri address)
    {
        Address = address;
    }

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DisposeAsync()
    {
        return new ValueTask();
    }

    public Uri Address { get; }
    public ValueTask StopAsync()
    {
        return new ValueTask();
    }
}