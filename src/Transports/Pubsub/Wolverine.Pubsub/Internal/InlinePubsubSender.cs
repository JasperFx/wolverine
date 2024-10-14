using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class InlinePubsubSender : ISender {
    private readonly PubsubTopic _endpoint;
    private readonly IOutgoingMapper<PubsubMessage> _mapper;
    private readonly ILogger _logger;
    private readonly CancellationToken _cancellationToken;

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => _endpoint.Uri;

    public InlinePubsubSender(
        PubsubTopic endpoint,
        IOutgoingMapper<PubsubMessage> mapper,
        ILogger logger,
        CancellationToken cancellationToken
    ) {
        _endpoint = endpoint;
        _mapper = mapper;
        _logger = logger;
        _cancellationToken = cancellationToken;
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

    public async ValueTask SendAsync(Envelope envelope) {
        if (_endpoint.Transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        await _endpoint.InitializeAsync(_logger);

        var message = new PubsubMessage();

        _mapper.MapEnvelopeToOutgoing(envelope, message);

        await _endpoint.Transport.PublisherApiClient.PublishAsync(new() {
            TopicAsTopicName = _endpoint.TopicName,
            Messages = { message }
        }, _cancellationToken);
    }
}
