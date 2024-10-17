using Google.Cloud.PubSub.V1;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class PubsubTopic : PubsubEndpoint {
    public TopicName Name { get; }

    public PubsubTopic(
        string topicName,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(new($"{transport.Protocol}://{topicName}"), transport, role) {
        Name = new(transport.ProjectId, $"{PubsubTransport.SanitizePubsubName(topicName)}");
        EndpointName = topicName;
        IsListener = false;
    }

    public PubsubSubscription FindOrCreateSubscription(string? subscriptionName = null) {
        var existing = _transport.Subscriptions.FirstOrDefault(x => x.Uri.OriginalString == $"{Uri.OriginalString}/{subscriptionName ?? EndpointName}");

        if (existing != null) return existing;

        var subscription = new PubsubSubscription(subscriptionName ?? EndpointName, this, _transport);

        _transport.Subscriptions.Add(subscription);

        return subscription;
    }

    public override async ValueTask SetupAsync(ILogger logger) {
        if (_transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        try {
            await _transport.PublisherApiClient.CreateTopicAsync(Name);
        }
        catch (RpcException ex) {
            if (ex.StatusCode != StatusCode.AlreadyExists) {
                logger.LogError(ex, "Error trying to initialize Google Cloud Pub/Sub topic \"{Topic}\"", Name);

                throw;
            }

            logger.LogInformation("Google Cloud Pub/Sub topic \"{Topic}\" already exists", Name);
        }
        catch (Exception ex) {
            logger.LogError(ex, "Error trying to initialize Google Cloud Pub/Sub topic \"{Topic}\"", Name);

            throw;
        }
    }

    public override async ValueTask<bool> CheckAsync() {
        if (_transport.PublisherApiClient is null) return false;

        try {
            await _transport.PublisherApiClient.GetTopicAsync(Name);

            return true;
        }
        catch {
            return false;
        }
    }

    public override async ValueTask TeardownAsync(ILogger logger) {
        if (_transport.PublisherApiClient is null) return;

        await _transport.PublisherApiClient.DeleteTopicAsync(Name);
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver) => throw new NotSupportedException();

    protected override ISender CreateSender(IWolverineRuntime runtime) {
        if (_transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        if (Mode == EndpointMode.Inline) return new InlinePubsubSender(this, runtime);

        return new BatchedSender(
            this,
            new PubsubSenderProtocol(this, _transport.PublisherApiClient, runtime),
            runtime.DurabilitySettings.Cancellation,
            runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>()
        );
    }

    internal async Task SendMessageAsync(Envelope envelope, ILogger logger) {
        if (_transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        if (!_hasInitialized) await InitializeAsync(logger);

        var message = new PubsubMessage();

        Mapper.MapEnvelopeToOutgoing(envelope, message);

        await _transport.PublisherApiClient.PublishAsync(new() {
            TopicAsTopicName = Name,
            Messages = { message }
        });
    }
}
