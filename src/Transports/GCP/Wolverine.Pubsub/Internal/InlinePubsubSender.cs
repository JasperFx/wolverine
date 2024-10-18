using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class InlinePubsubSender : ISender {
    private readonly PubsubTopic _topic;
    private readonly ILogger _logger;

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => _topic.Uri;

    public InlinePubsubSender(
        PubsubTopic topic,
        IWolverineRuntime runtime
    ) {
        _topic = topic;
        _logger = runtime.LoggerFactory.CreateLogger<InlinePubsubSender>();
    }

    public async Task<bool> PingAsync() {
        var envelope = Envelope.ForPing(Destination);

        try {
            await SendAsync(envelope);

            return true;
        }
        catch (Exception) {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope) => await _topic.SendMessageAsync(envelope, _logger);
}
