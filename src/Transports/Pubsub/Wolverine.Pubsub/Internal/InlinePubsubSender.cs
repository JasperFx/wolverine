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
        PubsubTopic endpoint,
        IWolverineRuntime runtime
    ) {
        _topic = endpoint;
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

    public ValueTask SendAsync(Envelope envelope) => new ValueTask(_topic.SendMessageAsync(envelope, _logger));
}
