using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class InlineSqsSender : ISender
{
    private readonly ILogger _logger;
    private readonly AmazonSqsQueue _queue;

    public InlineSqsSender(IWolverineRuntime runtime, AmazonSqsQueue queue)
    {
        _queue = queue;
        _logger = runtime.LoggerFactory.CreateLogger<InlineSqsSender>();
    }

    public bool SupportsNativeScheduledSend { get; } = false;
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