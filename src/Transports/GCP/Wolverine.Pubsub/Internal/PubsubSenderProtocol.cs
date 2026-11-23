using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

internal class PubsubSenderProtocol : ISenderProtocol
{
    private readonly PublisherServiceApiClient _client;
    private readonly PubsubEndpoint _endpoint;
    private readonly ILogger<PubsubSenderProtocol> _logger;
    private readonly IPubsubEnvelopeMapper _mapper;

    public PubsubSenderProtocol(
        PubsubEndpoint endpoint,
        PublisherServiceApiClient client,
        IWolverineRuntime runtime
    )
    {
        _mapper = endpoint.BuildMapper(runtime);
        _endpoint = endpoint;
        _client = client;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>();
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _endpoint.InitializeAsync(_logger);

        try
        {
            // GH-5822. Never publish to a topic named after an existing subscription
            _endpoint.AssertKnowsItsTopic();

            var message = new PubsubMessage();

            _mapper.MapOutgoingToMessage(batch, message);

            await _client.PublishAsync(new PublishRequest
            {
                TopicAsTopicName = _endpoint.Server.Topic.Name,
                Messages = { message }
            });

            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception ex)
        {
            await callback.MarkProcessingFailureAsync(batch, ex);
        }
    }
}