using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class PubsubSenderProtocol : ISenderProtocol {
    private readonly IWolverineRuntime _runtime;
    private readonly PubsubTopic _endpoint;
    private readonly IOutgoingMapper<PubsubMessage> _mapper;
    private readonly ILogger<PubsubSenderProtocol> _logger;

    public PubsubSenderProtocol(
        IWolverineRuntime runtime,
        PubsubTopic endpoint,
        IOutgoingMapper<PubsubMessage> mapper
    ) {
        _runtime = runtime;
        _endpoint = endpoint;
        _mapper = mapper;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>();
    }

    public async Task SendBatchAsync(
        ISenderCallback callback,
        OutgoingMessageBatch batch
    ) {
        if (_endpoint.Transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        await _endpoint.InitializeAsync(_logger);

        var messages = new List<PubsubMessage>(batch.Messages.Count);

        foreach (var envelope in batch.Messages) {
            try {
                var message = new PubsubMessage();

                _mapper.MapEnvelopeToOutgoing(envelope, message);

                messages.Add(message);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "{Uril}: Error trying to translate envelope \"{Envelope}\" to a PubsubMessage object. Message will be discarded.", _endpoint.Uri, envelope);
            }
        }

        try {
            await _endpoint.Transport.PublisherApiClient.PublishAsync(new() {
                TopicAsTopicName = _endpoint.TopicName,
                Messages = { messages }
            }, _runtime.Cancellation);

            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception ex) {
            await callback.MarkProcessingFailureAsync(batch, ex);
        }
    }
}
