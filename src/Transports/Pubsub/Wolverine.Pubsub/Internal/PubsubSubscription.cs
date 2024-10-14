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
    private bool _hasInitialized = false;

    public readonly SubscriptionName SubscriptionName;
    public readonly PubsubSubscriptionOptions Options;
    public readonly PubsubTopic TopicEndpoint;

    public PubsubSubscription(
        string subscriptionName,
        PubsubTopic topic,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(new($"{transport.Protocol}://topic/{topic.EndpointName}/{subscriptionName}"), transport, role) {
        SubscriptionName = new(transport.ProjectId, $"w-{subscriptionName}");
        TopicEndpoint = topic;
        Options = new PubsubSubscriptionOptions() {
            DeadLetterName = PubsubTransport.DeadLetterEndpointName
        };
        EndpointName = subscriptionName;
        IsListener = true;
    }

    public override async ValueTask InitializeAsync(ILogger logger) {
        if (_hasInitialized) return;
        if (Transport.AutoProvision) await SetupAsync(logger);

        _hasInitialized = true;
    }

    public override async ValueTask SetupAsync(ILogger logger) {
        if (Transport.SubscriberApiClient is null || Transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        try {
            var request = new Subscription {
                SubscriptionName = SubscriptionName,
                TopicAsTopicName = TopicEndpoint.TopicName,
            };

            if (Options.DeadLetterName.IsNotEmpty() && !Transport.Options.DisableDeadLetter)
                request.DeadLetterPolicy = new DeadLetterPolicy {
                    DeadLetterTopic = Transport.Topics[Options.DeadLetterName].TopicName.ToString(),
                    MaxDeliveryAttempts = Options.DeadLetterMaxDeliveryAttempts
                };

            await Transport.SubscriberApiClient.CreateSubscriptionAsync(request);
        }
        catch (RpcException ex) {
            if (ex.StatusCode != StatusCode.AlreadyExists) throw new WolverinePubsubTransportException($"{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{SubscriptionName}\" to topic \"{TopicEndpoint.TopicName}\"", ex);

            logger.LogInformation("{Uri}: Google Cloud Pub/Sub subscription \"{Subscription}\" already exists", Uri, SubscriptionName);
        }
        catch (Exception ex) {
            throw new WolverinePubsubTransportException($"{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{SubscriptionName}\" to topic \"{TopicEndpoint.TopicName}\"", ex);
        }
    }

    public ValueTask PurgeAsync(ILogger logger) => ValueTask.CompletedTask;

    public override async ValueTask<bool> CheckAsync() {
        try {
            if (Transport.SubscriberApiClient is null) return false;

            await Transport.SubscriberApiClient.GetSubscriptionAsync(SubscriptionName);

            return true;
        }
        catch {
            return false;
        }
    }

    public ValueTask<Dictionary<string, string>> GetAttributesAsync() => ValueTask.FromResult(new Dictionary<string, string>());

    public override ValueTask TeardownAsync(ILogger logger) {
        var task = Transport.SubscriberApiClient?.DeleteSubscriptionAsync(SubscriptionName) ?? Task.CompletedTask;

        return new ValueTask(task);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver) {
        var requeuer = Transport.RetryTopic?.BuildInlineSender(runtime) ?? TopicEndpoint.BuildInlineSender(runtime);
        var mapper = BuildMapper();

        if (Mode == EndpointMode.Inline) return ValueTask.FromResult<IListener>(new InlinePubsubListener(
            this,
            runtime.LoggerFactory.CreateLogger<InlinePubsubListener>(),
            receiver,
            requeuer,
            mapper
        ));

        return ValueTask.FromResult<IListener>(new BatchedPubsubListener(
            this,
            runtime.LoggerFactory.CreateLogger<BatchedPubsubListener>(),
            receiver,
            requeuer,
            mapper
        ));
    }

    protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

    internal void ConfigureDeadLetter(Action<PubsubSubscription> configure) {
        if (Options.DeadLetterName.IsEmpty()) return;

        configure(Transport.Topics[Options.DeadLetterName].FindOrCreateSubscription());
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender) {
        if (Options.DeadLetterName.IsEmpty() || Transport.Options.DisableDeadLetter) {
            deadLetterSender = default;

            return false;
        }

        var dlTopic = Transport.Topics[Options.DeadLetterName];

        deadLetterSender = new InlinePubsubSender(dlTopic, BuildMapper(), runtime.LoggerFactory.CreateLogger<InlinePubsubSender>(), runtime.Cancellation);

        return true;
    }
}
