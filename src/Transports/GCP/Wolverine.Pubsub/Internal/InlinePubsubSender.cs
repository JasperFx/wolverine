using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class InlinePubsubSender : ISender
{
    private readonly PubsubEndpoint _endpoint;
    private readonly ILogger _logger;

    public InlinePubsubSender(
        PubsubEndpoint endpoint,
        IWolverineRuntime runtime
    )
    {
        _endpoint = endpoint;
        _logger = runtime.LoggerFactory.CreateLogger<InlinePubsubSender>();
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