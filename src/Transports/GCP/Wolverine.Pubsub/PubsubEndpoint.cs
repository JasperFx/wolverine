using Google.Api.Gax;
using Google.Api.Gax.Grpc;
using Google.Cloud.PubSub.V1;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Pubsub;

public class PubsubEndpoint : Endpoint<IPubsubEnvelopeMapper, PubsubEnvelopeMapper>, IBrokerQueue
{
    private readonly PubsubTransport _transport;

    private bool _hasInitialized;
    public PubsubClientOptions Client = new();
    internal bool IsExistingSubscription = false;

    protected override PubsubEnvelopeMapper buildMapper(IWolverineRuntime runtime)
    {
        return new PubsubEnvelopeMapper(this);
    }

    /// <summary>
    ///     Name of the dead letter for this Google Cloud Platform Pub/Sub subcription where failed messages will be moved
    /// </summary>
    public string? DeadLetterName;

    internal bool IsDeadLetter;

    public PubsubServerOptions Server = new();

    public PubsubEndpoint(
        string topicName,
        PubsubTransport transport,
        EndpointRole role = EndpointRole.Application
    ) : base(new Uri($"{transport.Protocol}://{transport.ProjectId}/{topicName}"), role)
    {
        if (!PubsubTransport.NameRegex.IsMatch(topicName))
        {
            throw new WolverinePubsubInvalidEndpointNameException(topicName);
        }

        _transport = transport;

        Server.Topic.Name = new TopicName(transport.ProjectId, topicName);
        Server.Subscription.Name = new SubscriptionName(
            transport.ProjectId,
            _transport.IdentifierPrefix.IsNotEmpty() && !topicName.StartsWith($"{_transport.IdentifierPrefix}.")
                ? _transport.MaybeCorrectName(topicName)
                : topicName
        );
        EndpointName = topicName;

        if (transport.DeadLetter.Enabled)
        {
            DeadLetterName = PubsubTransport.DeadLetterName;
        }
    }

    public async ValueTask SetupAsync(ILogger logger)
    {
        if (_transport.PublisherApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        if (IsExistingSubscription)
        {
            if (!IsListener && !IsDeadLetter)
            {
                return;
            }

            if (_transport.SubscriberApiClient is null)
            {
                throw new WolverinePubsubTransportNotConnectedException();
            }

            try
            {
                await _transport.SubscriberApiClient.GetSubscriptionAsync(Server.Subscription.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "{Uri}: Error trying to verify Google Cloud Platform Pub/Sub subscription \"{Subscription}\"",
                    Uri, Server.Subscription.Name);

                throw;
            }

            return;
        }

        try
        {
            await _transport.PublisherApiClient.CreateTopicAsync(new Topic
            {
                TopicName = Server.Topic.Name,
                MessageRetentionDuration = Server.Topic.Options.MessageRetentionDuration
            });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            logger.LogInformation("{Uri}: Google Cloud Platform Pub/Sub topic \"{Topic}\" already exists", Uri,
                Server.Topic.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub topic \"{Topic}\"",
                Uri, Server.Topic.Name);

            throw;
        }

        if (!IsListener && !IsDeadLetter)
        {
            return;
        }

        if (_transport.SubscriberApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        try
        {
            if (!IsDeadLetter)
            {
                Server.Subscription.Name =
                    Server.Subscription.Name.WithAssignedNodeNumber(_transport.AssignedNodeNumber);
            }

            var request = new Subscription
            {
                SubscriptionName = Server.Subscription.Name,
                TopicAsTopicName = Server.Topic.Name,
                AckDeadlineSeconds = Server.Subscription.Options.AckDeadlineSeconds,
                EnableExactlyOnceDelivery = Server.Subscription.Options.EnableExactlyOnceDelivery,
                EnableMessageOrdering = Server.Subscription.Options.EnableMessageOrdering,
                MessageRetentionDuration = Server.Subscription.Options.MessageRetentionDuration,
                RetainAckedMessages = Server.Subscription.Options.RetainAckedMessages,
                RetryPolicy = Server.Subscription.Options.RetryPolicy
            };

            if (Server.Subscription.Options.DeadLetterPolicy is not null)
            {
                request.DeadLetterPolicy = Server.Subscription.Options.DeadLetterPolicy;
            }

            if (Server.Subscription.Options.ExpirationPolicy is not null)
            {
                request.ExpirationPolicy = Server.Subscription.Options.ExpirationPolicy;
            }

            if (Server.Subscription.Options.Filter is not null)
            {
                request.Filter = Server.Subscription.Options.Filter;
            }

            await _transport.SubscriberApiClient.CreateSubscriptionAsync(request);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            logger.LogInformation("{Uri}: Google Cloud Platform Pub/Sub subscription \"{Subscription}\" already exists",
                Uri, Server.Subscription.Name);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"",
                Uri, Server.Subscription.Name, Server.Topic.Name);

            throw;
        }
    }

    public async ValueTask<bool> CheckAsync()
    {
        if (
            _transport.PublisherApiClient is null ||
            _transport.SubscriberApiClient is null
        )
        {
            return false;
        }

        try
        {
            if (!IsExistingSubscription)
            {
                await _transport.PublisherApiClient.GetTopicAsync(Server.Topic.Name);
            }

            if (IsListener || IsDeadLetter)
            {
                await _transport.SubscriberApiClient.GetSubscriptionAsync(Server.Subscription.Name);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public ValueTask<Dictionary<string, string>> GetAttributesAsync()
    {
        return ValueTask.FromResult(new Dictionary<string, string>());
    }

    public async ValueTask PurgeAsync(ILogger logger)
    {
        if (_transport.SubscriberApiClient is null || !IsListener)
        {
            return;
        }

        try
        {
            var response = await _transport.SubscriberApiClient.PullAsync(
                Server.Subscription.Name,
                50,
                CallSettings.FromExpiration(Expiration.FromTimeout(TimeSpan.FromSeconds(2)))
            );

            if (!response.ReceivedMessages.Any())
            {
                return;
            }

            await _transport.SubscriberApiClient.AcknowledgeAsync(
                Server.Subscription.Name,
                response.ReceivedMessages.Select(x => x.AckId)
            );
        }
        catch (Exception ex)
        {
            logger.LogDebug(
                ex,
                "{Uri}: Error trying to purge Google Cloud Platform Pub/Sub subscription {Subscription}",
                Uri,
                Server.Subscription.Name
            );
        }
    }

    public async ValueTask TeardownAsync(ILogger logger)
    {
        if (IsExistingSubscription) { return; }

        if (_transport.SubscriberApiClient is not null && IsListener)
        {
            await _transport.SubscriberApiClient.DeleteSubscriptionAsync(Server.Subscription.Name);
        }

        if (_transport.PublisherApiClient is not null)
        {
            await _transport.PublisherApiClient.DeleteTopicAsync(Server.Topic.Name);
        }
    }

    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (IsExistingSubscription)
        {
            _hasInitialized = true;
        }
        if (_hasInitialized)
        {
            return;
        }

        try
        {
            if (_transport.AutoProvision)
            {
                await SetupAsync(logger);
            }

            if (_transport.AutoPurgeAllQueues)
            {
                await PurgeAsync(logger);
            }
        }
        catch (Exception ex)
        {
            throw new WolverinePubsubTransportException(
                $"{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub endpoint", ex);
        }

        _hasInitialized = true;
    }

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        if (Mode == EndpointMode.Inline)
        {
            return ValueTask.FromResult<IListener>(new InlinePubsubListener(
                this,
                _transport,
                receiver,
                runtime
            ));
        }

        return ValueTask.FromResult<IListener>(new BatchedPubsubListener(
            this,
            _transport,
            receiver,
            runtime
        ));
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        if (DeadLetterName.IsNotEmpty())
        {
            var initialized = _transport.Topics.Contains(DeadLetterName);
            var dl = _transport.Topics[DeadLetterName];

            if (!initialized)
            {
                dl.Server.Topic.Options = _transport.DeadLetter.Topic;
                dl.Server.Subscription.Options = _transport.DeadLetter.Subscription;
            }

            dl.DeadLetterName = null;
            dl.Server.Subscription.Options.DeadLetterPolicy = null;
            dl.IsDeadLetter = true;

            deadLetterSender = new InlinePubsubSender(dl, runtime);

            return true;
        }

        deadLetterSender = default;

        return false;
    }

    internal async Task SendMessageAsync(Envelope envelope, ILogger logger)
    {
        if (_transport.PublisherApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        if (!_hasInitialized)
        {
            await InitializeAsync(logger);
        }

        var message = new PubsubMessage();
        var orderBy = Server.Topic.OrderBy(envelope);

        EnvelopeMapper ??= new PubsubEnvelopeMapper(this);
        EnvelopeMapper.MapEnvelopeToOutgoing(envelope, message);

        message.OrderingKey = envelope.GroupId ?? orderBy ?? message.OrderingKey;

        await _transport.PublisherApiClient.PublishAsync(new PublishRequest
        {
            TopicAsTopicName = Server.Topic.Name,
            Messages = { message }
        });
    }

    internal void ConfigureDeadLetter(Action<PubsubEndpoint> configure)
    {
        if (DeadLetterName.IsEmpty())
        {
            return;
        }

        var initialized = _transport.Topics.Contains(DeadLetterName);
        var dl = _transport.Topics[DeadLetterName];

        if (!initialized)
        {
            dl.Server.Topic.Options = _transport.DeadLetter.Topic;
            dl.Server.Subscription.Options = _transport.DeadLetter.Subscription;
        }

        configure(dl);

        dl.DeadLetterName = null;
        dl.Server.Subscription.Options.DeadLetterPolicy = null;
        dl.IsDeadLetter = true;
    }

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        if (_transport.PublisherApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        if (Mode == EndpointMode.Inline)
        {
            return new InlinePubsubSender(this, runtime);
        }

        return new BatchedSender(
            this,
            new PubsubSenderProtocol(this, _transport.PublisherApiClient, runtime),
            runtime.DurabilitySettings.Cancellation,
            runtime.LoggerFactory.CreateLogger<PubsubSenderProtocol>()
        );
    }

    protected override bool supportsMode(EndpointMode mode)
    {
        return true;
    }
}