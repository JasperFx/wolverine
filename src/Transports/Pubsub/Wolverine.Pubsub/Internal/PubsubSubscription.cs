using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class PubsubSubscription : PubsubEndpoint, IBrokerQueue {
    public readonly SubscriptionName Name;
    public readonly PubsubSubscriptionOptions Options;
    public readonly PubsubTopic Topic;

    public PubsubSubscription(
        string subscriptionName,
        PubsubTopic topic,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(new($"{transport.Protocol}://{topic.EndpointName}/{subscriptionName}"), transport, role) {
        Name = new(transport.ProjectId, $"{PubsubTransport.SanitizePubsubName(subscriptionName)}");
        Topic = topic;
        Options = new PubsubSubscriptionOptions() {
            // DeadLetterName = PubsubTransport.DeadLetterEndpointName
        };
        EndpointName = subscriptionName;
        IsListener = true;
    }

    public override async ValueTask SetupAsync(ILogger logger) {
        if (_transport.SubscriberApiClient is null || _transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        try {
            var request = new Subscription {
                SubscriptionName = Name,
                TopicAsTopicName = Topic.Name,
            };

            // if (Options.DeadLetterName.IsNotEmpty() && !_transport.Options.DisableDeadLetter)
            //     request.DeadLetterPolicy = new DeadLetterPolicy {
            //         DeadLetterTopic = new TopicName(_transport.ProjectId, Options.DeadLetterName).ToString(),
            //         MaxDeliveryAttempts = Options.DeadLetterMaxDeliveryAttempts
            //     };

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

    protected override ISender CreateSender(IWolverineRuntime runtime) => throw new NotSupportedException();

    // internal void ConfigureDeadLetter(Action<PubsubSubscription> configure) {
    //     if (Options.DeadLetterName.IsEmpty()) return;

    //     configure(_transport.Topics[Options.DeadLetterName].FindOrCreateSubscription());
    // }
}
