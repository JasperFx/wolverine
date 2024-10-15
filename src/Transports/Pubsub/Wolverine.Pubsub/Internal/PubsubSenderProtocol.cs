using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

internal class PubsubSenderProtocol : ISenderProtocol {
    private readonly PubsubTopic _topic;
    private readonly PublisherServiceApiClient _client;
    private readonly IWolverineRuntime _runtime;
    private readonly ILogger<PubsubSenderProtocol> _logger;

    public PubsubSenderProtocol(
        PubsubTopic topic,
        PublisherServiceApiClient client,
        IWolverineRuntime runtime
    ) {
        _topic = topic;
        _client = client;
        _runtime = runtime;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>();
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch) {
        await _topic.InitializeAsync(_logger);

        var messages = new List<PubsubMessage>();
        var successes = new List<Envelope>();
        var fails = new List<Envelope>();

        foreach (var envelope in batch.Messages) {
            try {
                var message = new PubsubMessage();

                _topic.Mapper.MapEnvelopeToOutgoing(envelope, message);

                messages.Add(message);
                successes.Add(envelope);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "{Uril}: Error while mapping envelope \"{Envelope}\" to a PubsubMessage object.", _topic.Uri, envelope);

                fails.Add(envelope);
            }
        }

        try {
            await _client.PublishAsync(new() {
                TopicAsTopicName = _topic.Name,
                Messages = { messages }
            }, _runtime.Cancellation);

            await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, successes));

            if (fails.Any())
                await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, fails));
        }
        catch (Exception ex) {
            await callback.MarkProcessingFailureAsync(batch, ex);
        }
    }
}
