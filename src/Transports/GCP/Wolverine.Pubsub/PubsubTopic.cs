using Google.Cloud.PubSub.V1;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub;

public class PubsubTopic : PubsubEndpoint, ISendOnlyEndpoint
{
    public string TopicId { get; }
    
    public LightweightCache<string, PubsubSubscription> GcpSubscriptions { get; }

    public PubsubTopic(PubsubTransport parent, string topicId, EndpointRole role) : base(parent, $"{PubsubTransport.ProtocolName}://{topicId}".ToUri(), role)
    {
        Parent = parent;
        TopicId = topicId;

        Mode = EndpointMode.Inline;

        GcpSubscriptions = new LightweightCache<string, PubsubSubscription>(name => new PubsubSubscription(this, name));
    }

    internal PubsubTransport Parent { get; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException("You can only send to GCP Pub/Sub topics. You will need a subscription to listen to a topic");
    }

    public TopicName TopicName => new TopicName(Parent.ProjectId, TopicId);

    public TimeSpan MessageRetentionDuration { get; set; } = 7.Days();

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        switch (Mode)
        {
            case EndpointMode.Inline:
                return new PubsubInlineSender(this, runtime);
            case EndpointMode.Durable:
                return new BatchedSender(this, new PubsubSenderProtocol(this, runtime), runtime.DurabilitySettings.Cancellation, runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>());
            default:
                throw new ArgumentOutOfRangeException(nameof(Mode),
                    $"The GCP Pubsub transport cannot (yet) support {nameof(EndpointMode.BufferedInMemory)}");
        }
    }

    public override async ValueTask<bool> CheckAsync()
    {
        return await Parent.WithPublisherServiceApiClient(async client =>
        {
            try
            {
                await client.GetTopicAsync(TopicName);
                return true;
            }
            catch
            {
                return false;
            }
        });

    }

    public override async ValueTask TeardownAsync(ILogger logger)
    {
        foreach (var subscription in GcpSubscriptions)
        {
            await subscription.TeardownAsync(logger);
        }

        await Parent.WithPublisherServiceApiClient(async client =>
        {
            await client.DeleteTopicAsync(TopicName);
            return true;
        });
    }

    private bool _hasSetup;
    public override async ValueTask SetupAsync(ILogger logger)
    {
        if (_hasSetup) return;
        await Parent.WithPublisherServiceApiClient(async client =>
        {
            try
            {
                await client.CreateTopicAsync(new Topic
                {
                    TopicName = TopicName,
                    MessageRetentionDuration = Duration.FromTimeSpan(MessageRetentionDuration)
                });
            }
            catch (RpcException ex)
            {
                if (ex.StatusCode != StatusCode.AlreadyExists)
                {
                    logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub topic \"{Topic}\"",
                        Uri, TopicName);

                    throw;
                }

                logger.LogDebug("{Uri}: Google Cloud Platform Pub/Sub topic \"{Topic}\" already exists", Uri,
                    TopicName);
            }
            catch (Exception e)
            {
                logger.LogError(e, "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub topic \"{Topic}\"",
                    Uri, TopicName);

                throw;
            }

            return true;
        });

        _hasSetup = true;
    }
}