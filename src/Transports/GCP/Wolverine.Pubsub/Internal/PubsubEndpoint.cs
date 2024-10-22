using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Google.Protobuf;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class PubsubEndpoint : Endpoint, IBrokerQueue {
    private IPubsubEnvelopeMapper? _mapper;
    private readonly PubsubTransport _transport;

    private bool _hasInitialized = false;

    public PubsubServerOptions Server = new();
    public PubsubClientOptions Client = new();

    /// <summary>
    /// Name of the dead letter for this Google Cloud Pub/Sub subcription where failed messages will be moved
    /// </summary>
    public string? DeadLetterName = PubsubTransport.DeadLetterName;

    /// <summary>
    ///     Pluggable strategy for interoperability with non-Wolverine systems. Customizes how the incoming Google Cloud Pub/Sub messages
    ///     are read and how outgoing messages are written to Google Cloud Pub/Sub.
    /// </summary>
    public IPubsubEnvelopeMapper Mapper {
        get {
            if (_mapper is not null) return _mapper;

            var mapper = new PubsubEnvelopeMapper(this);

            // Important for interoperability
            if (MessageType != null) mapper.ReceivesMessage(MessageType);

            _mapper = mapper;

            return _mapper;
        }
        set => _mapper = value;
    }

    public PubsubEndpoint(
        string topicName,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(new($"{transport.Protocol}://{transport.ProjectId}/{topicName}"), role) {
        if (!PubsubTransport.NameRegex.IsMatch(topicName)) throw new WolverinePubsubInvalidEndpointNameException(topicName);

        _transport = transport;

        Server.Topic.Name = new(transport.ProjectId, topicName);
        Server.Subscription.Name = new(
            transport.ProjectId,
            _transport.IdentifierPrefix.IsNotEmpty() &&
            topicName.StartsWith($"{_transport.IdentifierPrefix}.")
                ? _transport.MaybeCorrectName(topicName.Substring(_transport.IdentifierPrefix.Length + 1))
                : topicName
        );
        EndpointName = topicName;
    }

    public override async ValueTask InitializeAsync(ILogger logger) {
        if (_hasInitialized) return;

        try {
            if (_transport.AutoProvision) await SetupAsync(logger);
            if (_transport.AutoPurgeAllQueues) await PurgeAsync(logger);
        }
        catch (Exception ex) {
            throw new WolverinePubsubTransportException($"{Uri}: Error trying to initialize Google Cloud Pub/Sub endpoint", ex);
        }

        _hasInitialized = true;
    }

    public async ValueTask SetupAsync(ILogger logger) {
        if (_transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        try {
            var request = new Topic {
                TopicName = Server.Topic.Name,
                MessageRetentionDuration = Server.Topic.Options.MessageRetentionDuration
            };

            await _transport.PublisherApiClient.CreateTopicAsync(request);
        }
        catch (RpcException ex) {
            if (ex.StatusCode != StatusCode.AlreadyExists) {
                logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Pub/Sub topic \"{Topic}\"", Uri, Server.Topic.Name);

                throw;
            }

            logger.LogInformation("{Uri}: Google Cloud Pub/Sub topic \"{Topic}\" already exists", Uri, Server.Topic.Name);
        }
        catch (Exception ex) {
            logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Pub/Sub topic \"{Topic}\"", Uri, Server.Topic.Name);

            throw;
        }

        if (!IsListener) return;

        if (_transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        Server.Subscription.Name = Server.Subscription.Name.WithAssignedNodeNumber(_transport.AssignedNodeNumber);

        try {
            var request = new Subscription {
                SubscriptionName = Server.Subscription.Name,
                TopicAsTopicName = Server.Topic.Name,
                AckDeadlineSeconds = Server.Subscription.Options.AckDeadlineSeconds,
                EnableExactlyOnceDelivery = Server.Subscription.Options.EnableExactlyOnceDelivery,
                EnableMessageOrdering = Server.Subscription.Options.EnableMessageOrdering,
                MessageRetentionDuration = Server.Subscription.Options.MessageRetentionDuration,
                RetainAckedMessages = Server.Subscription.Options.RetainAckedMessages,
                RetryPolicy = Server.Subscription.Options.RetryPolicy
            };

            if (Server.Subscription.Options.DeadLetterPolicy is not null) request.DeadLetterPolicy = Server.Subscription.Options.DeadLetterPolicy;
            if (Server.Subscription.Options.ExpirationPolicy is not null) request.ExpirationPolicy = Server.Subscription.Options.ExpirationPolicy;
            if (Server.Subscription.Options.Filter is not null) request.Filter = Server.Subscription.Options.Filter;

            await _transport.SubscriberApiClient.CreateSubscriptionAsync(request);
        }
        catch (RpcException ex) {
            if (ex.StatusCode != StatusCode.AlreadyExists) {
                logger.LogError(
                    ex,
                    "{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"",
                    Uri,
                    Server.Subscription.Name,
                    Server.Topic.Name
                );

                throw;
            }

            logger.LogInformation(
                "{Uri}: Google Cloud Pub/Sub subscription \"{Subscription}\" already exists",
                Uri,
                Server.Subscription.Name
            );
        }
        catch (Exception ex) {
            logger.LogError(
                ex,
                "{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"",
                Uri,
                Server.Subscription.Name,
                Server.Topic.Name
            );

            throw;
        }
    }

    public ValueTask<bool> CheckAsync() {
        if (
            _transport.PublisherApiClient is null ||
            _transport.SubscriberApiClient is null
        ) return ValueTask.FromResult(false);

        return ValueTask.FromResult(_hasInitialized);
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

    public ValueTask<Dictionary<string, string>> GetAttributesAsync() => ValueTask.FromResult(new Dictionary<string, string>());

    // public ValueTask PurgeAsync(ILogger logger) => ValueTask.CompletedTask;

    public async ValueTask PurgeAsync(ILogger logger) {
        if (_transport.SubscriberApiClient is null || !IsListener) return;

        try {
            var response = await _transport.SubscriberApiClient.PullAsync(
                Server.Subscription.Name,
                maxMessages: 50,
                CallSettings.FromExpiration(Expiration.FromTimeout(TimeSpan.FromSeconds(2)))
            );

            if (!response.ReceivedMessages.Any()) return;

            await _transport.SubscriberApiClient.AcknowledgeAsync(
                Server.Subscription.Name,
                response.ReceivedMessages.Select(x => x.AckId)
            );
        }
        catch (Exception ex) {
            logger.LogDebug(
                ex,
                "{Uri}: Error trying to purge Google Cloud Pub/Sub subscription {Subscription}",
                Uri,
                Server.Subscription.Name
            );
        }
    }

    public async ValueTask TeardownAsync(ILogger logger) {
        if (_transport.SubscriberApiClient is not null && IsListener)
            await _transport.SubscriberApiClient.DeleteSubscriptionAsync(Server.Subscription.Name);

        if (_transport.PublisherApiClient is not null) await _transport.PublisherApiClient.DeleteTopicAsync(Server.Topic.Name);
    }

    internal async Task SendMessageAsync(Envelope envelope, ILogger logger) {
        if (_transport.PublisherApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

        if (!_hasInitialized) await InitializeAsync(logger);

        var message = new PubsubMessage();

        Mapper.MapEnvelopeToOutgoing(envelope, message);

        await _transport.PublisherApiClient.PublishAsync(new() {
            TopicAsTopicName = Server.Topic.Name,
            Messages = { message }
        });
    }

    internal void ConfigureDeadLetter(Action<PubsubEndpoint> configure) {
        if (DeadLetterName.IsEmpty()) return;

        var dl = _transport.Topics[DeadLetterName];

        dl.DeadLetterName = null;
        dl.Server.Subscription.Options.DeadLetterPolicy = null;
        dl.IsListener = true;

        configure(dl);
    }

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

    protected override bool supportsMode(EndpointMode mode) => true;
}
