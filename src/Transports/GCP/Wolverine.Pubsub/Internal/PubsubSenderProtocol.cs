using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

internal class PubsubSenderProtocol : ISenderProtocol {
    private readonly PubsubEndpoint _endpoint;
    private readonly PublisherServiceApiClient _client;
    private readonly ILogger<PubsubSenderProtocol> _logger;

    public PubsubSenderProtocol(
        PubsubEndpoint endpoint,
        PublisherServiceApiClient client,
        IWolverineRuntime runtime
    ) {
        _endpoint = endpoint;
        _client = client;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>();
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch) {
        await _endpoint.InitializeAsync(_logger);

        var messages = new List<PubsubMessage>();
        var successes = new List<Envelope>();
        var fails = new List<Envelope>();

        foreach (var envelope in batch.Messages) {
            try {
                var message = new PubsubMessage();

                _endpoint.Mapper.MapEnvelopeToOutgoing(envelope, message);

                messages.Add(message);
                successes.Add(envelope);
            }
            catch (Exception ex) {
                _logger.LogError(ex, "{Uril}: Error while mapping envelope \"{Envelope}\" to a PubsubMessage object.", _endpoint.Uri, envelope);

                fails.Add(envelope);
            }
        }

        try {
            await _client.PublishAsync(new() {
                TopicAsTopicName = _endpoint.Server.Topic.Name,
                Messages = { messages }
            });

            await callback.MarkSuccessfulAsync(new OutgoingMessageBatch(batch.Destination, successes));

            if (fails.Any())
                await callback.MarkProcessingFailureAsync(new OutgoingMessageBatch(batch.Destination, fails));
        }
        catch (Exception ex) {
            await callback.MarkProcessingFailureAsync(batch, ex);
        }
    }
}
