using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.AmazonSqs.Internal;

internal class InlineAmazonSender : ISender
{
    private readonly ILogger _logger;
    private readonly AmazonEndpoint _endpoint;

    public InlineAmazonSender(IWolverineRuntime runtime, AmazonEndpoint endpoint)
    {
        _endpoint = endpoint;
        _logger = runtime.LoggerFactory.CreateLogger<InlineAmazonSender>();
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => _endpoint.Uri;

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
        await _endpoint.SendMessageAsync(envelope, _logger);
    }
}
