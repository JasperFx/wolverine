using Google.Cloud.PubSub.V1;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

internal class PubsubSenderProtocol : ISenderProtocol
{
    private PublisherClient? _client;
    private readonly PubsubTopic _topic;
    private readonly ILogger<PubsubSenderProtocol> _logger;
    private readonly IPubsubEnvelopeMapper _mapper;

    public PubsubSenderProtocol(
        PubsubTopic topic,
        IWolverineRuntime runtime
    )
    {
        _mapper = topic.BuildMapper(runtime);
        _topic = topic;
        _logger = runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>();
    }

    public async Task SendBatchAsync(ISenderCallback callback, OutgoingMessageBatch batch)
    {
        await _topic.InitializeAsync(_logger);

        try
        {
            var request = new PublishRequest
            {
                TopicAsTopicName = _topic.TopicName
            };

            foreach (var envelope in batch.Messages)
            {
                var message = new PubsubMessage();
                _mapper.MapEnvelopeToOutgoing(envelope, message);
                request.Messages.Add(message);
            }
            
            if (_client == null)
            {
                var builder = new PublisherClientBuilder
                {
                    EmulatorDetection = _topic.Parent.EmulatorDetection,
                    TopicName = _topic.TopicName
                };

                _client = await builder.BuildAsync();
            }
            
            await _client.PublishAsync(request);
            await callback.MarkSuccessfulAsync(batch);
        }
        catch (Exception ex)
        {
            await callback.MarkProcessingFailureAsync(batch, ex);
        }
    }
}