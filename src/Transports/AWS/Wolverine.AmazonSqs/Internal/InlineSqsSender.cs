using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class InlineSqsSender : ISender, IConditionalNativeScheduling
{
    private readonly ILogger _logger;
    private readonly AmazonSqsQueue _queue;

    public InlineSqsSender(IWolverineRuntime runtime, AmazonSqsQueue queue)
    {
        _queue = queue;
        _logger = runtime.LoggerFactory.CreateLogger<InlineSqsSender>();
    }

    // Standard queues can delay individual messages natively (DelaySeconds, max 15 minutes);
    // FIFO queues only support a queue-level delay, so they never schedule natively
    public bool SupportsNativeScheduledSend => !_queue.IsFifoQueue;

    bool IConditionalNativeScheduling.CanScheduleNatively(Envelope envelope, DateTimeOffset utcNow)
    {
        return _queue.CanScheduleNatively(envelope, utcNow);
    }

    public Uri Destination => _queue.Uri;

    public async Task<bool> PingAsync()
    {
        var envelope = Envelope.ForPing(Destination);
        try
        {
            await SendAsync(envelope);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        await _queue.SendMessageAsync(envelope, _logger);
    }
}