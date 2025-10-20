using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub;

public class PubsubSubscription : PubsubEndpoint
{
    public PubsubSubscription(PubsubTopic topic, string name) : base(topic.Parent, new Uri($"{topic.Uri}{name}"),
        EndpointRole.Application)
    {
        Name = name;
        Topic = topic;
        IsListener = true;
        SubscriptionName = new SubscriptionName(Topic.Parent.ProjectId, Name);
    }

    public string Name { get; }

    public SubscriptionName SubscriptionName { get; }

    public Subscription Options { get; } = new();

    public PubsubTopic Topic { get; }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        return new ValueTask<IListener>(new PubsubListener(this, runtime, receiver));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException("You can only publish to GCP Pub/Sub topics");
    }

    public override async ValueTask<bool> CheckAsync()
    {
        var parentExists = await Topic.CheckAsync();
        if (!parentExists)
        {
            return false;
        }

        return await Topic.Parent.WithSubscriberServiceApiClient(async client =>
        {
            try
            {
                await client.GetSubscriptionAsync(SubscriptionName);
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
        await Topic.Parent.WithSubscriberServiceApiClient(async client =>
        {
            await client.DeleteSubscriptionAsync(SubscriptionName);
            return true;
        });
    }

    public override async ValueTask SetupAsync(ILogger logger)
    {
        // This is idempotent, so no worry
        await Topic.SetupAsync(logger);

        await Topic.Parent.WithSubscriberServiceApiClient(async client =>
        {
            Options.SubscriptionName = SubscriptionName;
            Options.TopicAsTopicName = Topic.TopicName;

            try
            {
                await client.CreateSubscriptionAsync(Options);
            }
            catch (RpcException ex)
            {
                if (ex.StatusCode != StatusCode.AlreadyExists)
                {
                    logger.LogError(ex,
                        "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"",
                        Uri, SubscriptionName, Topic.TopicName);

                    throw;
                }

                logger.LogInformation(
                    "{Uri}: Google Cloud Platform Pub/Sub subscription \"{Subscription}\" already exists",
                    Uri, SubscriptionName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"",
                    Uri, SubscriptionName, Topic.TopicName);

                throw;
            }

            return true;
        });
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return mode != EndpointMode.BufferedInMemory;
    }
}