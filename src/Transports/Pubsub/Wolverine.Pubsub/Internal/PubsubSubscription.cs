using Google.Cloud.PubSub.V1;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class PubsubSubscription : PubsubEndpoint, IBrokerQueue {
    public readonly SubscriptionName Name;
    public readonly PubsubTopic Topic;

    public int MaxRetryCount = 5;
    public int RetryDelay = 1000;

    /// <summary>
    /// Name of the dead letter queue for this SQS queue where failed messages will be moved
    /// </summary>
    public string? DeadLetterName = PubsubTransport.DeadLetterName;

    public PubsubSubscriptionOptions PubsubOptions = new PubsubSubscriptionOptions();

    public PubsubSubscription(
        string subscriptionName,
        PubsubTopic topic,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(new($"{transport.Protocol}://{topic.EndpointName}/{subscriptionName}"), transport, role) {
        Name = new(transport.ProjectId, $"{PubsubTransport.SanitizePubsubName(subscriptionName)}");
        Topic = topic;
        EndpointName = subscriptionName;
        IsListener = true;
    }

    public override async ValueTask SetupAsync(ILogger logger) {
        if (_transport.SubscriberApiClient is null || _transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        try {
            var request = new Subscription {
                SubscriptionName = Name,
                TopicAsTopicName = Topic.Name,
                AckDeadlineSeconds = PubsubOptions.AckDeadlineSeconds,
                EnableExactlyOnceDelivery = PubsubOptions.EnableExactlyOnceDelivery,
                EnableMessageOrdering = PubsubOptions.EnableMessageOrdering,
                RetainAckedMessages = PubsubOptions.RetainAckedMessages
            };

            if (PubsubOptions.DeadLetterPolicy is not null) request.DeadLetterPolicy = PubsubOptions.DeadLetterPolicy;
            if (PubsubOptions.ExpirationPolicy is not null) request.ExpirationPolicy = PubsubOptions.ExpirationPolicy;
            if (PubsubOptions.Filter is not null) request.Filter = PubsubOptions.Filter;
            if (PubsubOptions.MessageRetentionDuration is not null) request.MessageRetentionDuration = PubsubOptions.MessageRetentionDuration;
            if (PubsubOptions.RetryPolicy is not null) request.RetryPolicy = PubsubOptions.RetryPolicy;

            await _transport.SubscriberApiClient.CreateSubscriptionAsync(request);
        }
        catch (RpcException ex) {
            if (ex.StatusCode != StatusCode.AlreadyExists) {
                logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"", Uri, Name, Topic.Name);

                throw;
            }

            logger.LogInformation("{Uri}: Google Cloud Pub/Sub subscription \"{Subscription}\" already exists", Uri, Name);
        }
        catch (Exception ex) {
            logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"", Uri, Name, Topic.Name);

            throw;
        }
    }

    public ValueTask PurgeAsync(ILogger logger) => ValueTask.CompletedTask;

    public override async ValueTask<bool> CheckAsync() {
        if (_transport.SubscriberApiClient is null) return false;

        try {
            await _transport.SubscriberApiClient.GetSubscriptionAsync(Name);

            return true;
        }
        catch {
            return false;
        }
    }

    public ValueTask<Dictionary<string, string>> GetAttributesAsync() => ValueTask.FromResult(new Dictionary<string, string>());

    public override async ValueTask TeardownAsync(ILogger logger) {
        if (_transport.SubscriberApiClient is null) return;

        await _transport.SubscriberApiClient.DeleteSubscriptionAsync(Name);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver) {
        if (Mode == EndpointMode.Inline) return ValueTask.FromResult<IListener>(new InlinePubsubListener(
            this,
            _transport,
            receiver,
            runtime
        ));

        return ValueTask.FromResult<IListener>(new BatchedPubsubListener(
            this,
            _transport,
            receiver,
            runtime
        ));
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender) {
        if (DeadLetterName.IsNotEmpty() && _transport.EnableDeadLettering) {
            var dl = _transport.Topics[DeadLetterName];

            deadLetterSender = new InlinePubsubSender(dl, runtime);

            return true;
        }

        deadLetterSender = default;

        return false;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

    internal void ConfigureDeadLetter(Action<PubsubSubscription> configure) {
        if (DeadLetterName.IsEmpty()) return;

        configure(_transport.Topics[DeadLetterName].FindOrCreateSubscription());
    }
}
