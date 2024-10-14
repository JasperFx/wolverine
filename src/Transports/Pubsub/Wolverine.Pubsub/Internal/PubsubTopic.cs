using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class PubsubTopic : PubsubEndpoint {
    private bool _hasInitialized = false;

    public TopicName TopicName { get; }

    public PubsubTopic(
        string topicName,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(new($"{transport.Protocol}://topic/{topicName}"), transport, role) {
        TopicName = new(transport.ProjectId, $"w-{topicName}");
        EndpointName = topicName;
        IsListener = false;
    }

    public PubsubSubscription FindOrCreateSubscription(string? subscriptionName = null) {
        var existing = Transport.Subscriptions.FirstOrDefault(x =>
            x.SubscriptionName.ProjectId == Transport.ProjectId &&
            x.SubscriptionName.SubscriptionId == (subscriptionName ?? EndpointName) &&
            x.TopicEndpoint == this
        );

        if (existing != null) return existing;

        var subscription = new PubsubSubscription(subscriptionName ?? EndpointName, this, Transport, Role);

        Transport.Subscriptions.Add(subscription);

        return subscription;
    }

    public override async ValueTask InitializeAsync(ILogger logger) {
        if (_hasInitialized) return;
        if (Transport.AutoProvision) await SetupAsync(logger);

        _hasInitialized = true;
    }

    public override async ValueTask SetupAsync(ILogger logger) {
        if (Transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        try {
            await Transport.PublisherApiClient.CreateTopicAsync(TopicName);
        }
        catch (RpcException ex) {
            if (ex.StatusCode != StatusCode.AlreadyExists) logger.LogError(ex, "Error trying to initialize Google Cloud Pub/Sub topic \"{Topic}\"", TopicName);

            logger.LogInformation("Google Cloud Pub/Sub topic \"{Topic}\" already exists", TopicName);
        }
        catch (Exception ex) {
            logger.LogError(ex, "Error trying to initialize Google Cloud Pub/Sub topic \"{Topic}\"", TopicName);
        }
    }

    public override async ValueTask<bool> CheckAsync() {
        try {
            if (Transport.PublisherApiClient is null) return false;

            await Transport.PublisherApiClient.GetTopicAsync(TopicName);

            return true;
        }
        catch {
            return false;
        }
    }

    public override ValueTask TeardownAsync(ILogger logger) {
        var task = Transport.PublisherApiClient?.DeleteTopicAsync(TopicName) ?? Task.CompletedTask;

        return new ValueTask(task);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver) => throw new NotSupportedException();

    internal ISender BuildInlineSender(IWolverineRuntime runtime) => BuildInlineSender(
        runtime.LoggerFactory.CreateLogger<InlinePubsubSender>(),
        runtime.Cancellation
    );

    internal ISender BuildInlineSender(ILogger logger, CancellationToken cancellationToken) => new InlinePubsubSender(
        this,
        BuildMapper(),
        logger,
        cancellationToken
    );

    protected override ISender CreateSender(IWolverineRuntime runtime) {
        if (Mode == EndpointMode.Inline) return new InlinePubsubSender(
            this,
            BuildMapper(),
            runtime.LoggerFactory.CreateLogger<InlinePubsubSender>(),
            runtime.Cancellation
        );

        return new BatchedSender(
            this,
            new PubsubSenderProtocol(
                runtime,
                this,
                BuildMapper()
            ),
            runtime.DurabilitySettings.Cancellation,
            runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>()
        );
    }
}
