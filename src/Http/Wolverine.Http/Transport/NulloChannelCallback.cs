using Wolverine.Transports;

namespace Wolverine.Http.Transport;

internal struct NulloChannelCallback : IChannelCallback
{
    public ValueTask CompleteAsync(Envelope envelope)
    {
        return new ValueTask();
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return new ValueTask();
    }
}