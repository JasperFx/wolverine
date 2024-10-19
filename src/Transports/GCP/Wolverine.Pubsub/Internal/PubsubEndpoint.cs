using System.Diagnostics;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub.Internal;

public class PubsubEndpoint : Endpoint, IBrokerEndpoint, IBrokerQueue {
    private IPubsubEnvelopeMapper? _mapper;
    protected readonly PubsubTransport _transport;

    protected bool _hasInitialized = false;

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
        Server.Subscription.Name = new(transport.ProjectId, _transport.IdentifierPrefix.IsNotEmpty() && topicName.StartsWith($"{_transport.IdentifierPrefix}.") ? _transport.MaybeCorrectName(topicName.Substring(_transport.IdentifierPrefix.Length + 1)) : topicName);
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
            await _transport.PublisherApiClient.CreateTopicAsync(Server.Topic.Name);
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

        if (_transport.SubscriberApiClient is null) throw new WolverinePubsubTransportNotConnectedException();

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
                logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"", Uri, Server.Subscription.Name, Server.Topic.Name);

                throw;
            }

            logger.LogInformation("{Uri}: Google Cloud Pub/Sub subscription \"{Subscription}\" already exists", Uri, Server.Subscription.Name);
        }
        catch (Exception ex) {
            logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"", Uri, Server.Subscription.Name, Server.Topic.Name);

            throw;
        }
    }

    public async ValueTask<bool> CheckAsync() {
        if (_transport.PublisherApiClient is null) return false;

        try {
            await _transport.PublisherApiClient.GetTopicAsync(Server.Topic.Name);

            return true;
        }
        catch {
            return false;
        }
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

    public ValueTask PurgeAsync(ILogger logger) => ValueTask.CompletedTask;

    // public async ValueTask PurgeAsync(ILogger logger) {
    //     if (_transport.SubscriberApiClient is null) return;

    //     try {
    //         var stopwatch = new Stopwatch();

    //         stopwatch.Start();

    //         while (stopwatch.ElapsedMilliseconds < 2000) {
    //             var response = await _transport.SubscriberApiClient.PullAsync(
    //                 Server.Subscription.Name,
    //                 maxMessages: 50
    //             );

    //             if (!response.ReceivedMessages.Any()) return;

    //             await _transport.SubscriberApiClient.AcknowledgeAsync(
    //                 Server.Subscription.Name,
    //                 response.ReceivedMessages.Select(x => x.AckId)
    //             );
    //         };
    //     }
    //     catch (Exception e) {
    //         logger.LogDebug(e, "{Uri}: Error trying to purge Google Cloud Pub/Sub subscription {Subscription}", Uri, Server.Subscription.Name);
    //     }
    // }

    public async ValueTask TeardownAsync(ILogger logger) {
        if (_transport.PublisherApiClient is null || _transport.SubscriberApiClient is null) return;

        await _transport.SubscriberApiClient.DeleteSubscriptionAsync(Server.Subscription.Name);
        await _transport.PublisherApiClient.DeleteTopicAsync(Server.Topic.Name);
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

        configure(_transport.Topics[DeadLetterName]);
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
