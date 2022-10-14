using Wolverine.Transports;

namespace Wolverine.AmazonSqs.Internal;

internal class AmazonSqsChannelCallback : IChannelCallback
{
    public static readonly AmazonSqsChannelCallback Instance = new();

    private AmazonSqsChannelCallback()
    {
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await e.CompleteAsync();
        }
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        if (envelope is AmazonSqsEnvelope e)
        {
            await e.DeferAsync();
        }
    }
}