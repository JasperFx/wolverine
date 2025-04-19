using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSns.Internal;

internal class InlineSnsSender : ISender
{
    private readonly ILogger _logger;
    private readonly AmazonSnsTopic _topic;

    public InlineSnsSender(IWolverineRuntime runtime, AmazonSnsTopic topic)
    {
        _topic = topic;
        _logger = runtime.LoggerFactory.CreateLogger<InlineSnsSender>();
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => _topic.Uri;

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
        await _topic.SendMessageAsync(envelope, _logger);
    }
}
