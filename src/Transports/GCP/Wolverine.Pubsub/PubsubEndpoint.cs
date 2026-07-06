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

    internal PubsubTransport Transport => _transport;

    /// <summary>
    /// The topic name for this endpoint under the given project id. For the default project this is the endpoint's
    /// own <see cref="PubsubServerOptions.Topic" /> name; for a tenant project (broker-per-tenant) it is the same
    /// topic id under the tenant's project, yielding a physically distinct GCP topic.
    /// </summary>
    internal TopicName TopicNameFor(string projectId)
    {
        return projectId == _transport.ProjectId
            ? Server.Topic.Name
            : new TopicName(projectId, Server.Topic.Name.TopicId);
    }

    /// <summary>
    /// The subscription name for this endpoint under the given project id. Preserves the (possibly per-node)
    /// subscription id computed during <see cref="SetupAsync" />, just under the tenant's project.
    /// </summary>
    internal SubscriptionName SubscriptionNameFor(string projectId)
    {
        return projectId == _transport.ProjectId
            ? Server.Subscription.Name
            : new SubscriptionName(projectId, Server.Subscription.Name.SubscriptionId);
    }

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
        BrokerRole = "pubsub";

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

        // Only competing-consumer listeners get a per-node subscription so that each node
        // load balances a distinct copy of the stream. A leader-pinned (or otherwise
        // single-node) listener must read from one shared, cluster-stable subscription;
        // otherwise every node creates its own subscription and Pub/Sub fans a copy of every
        // message to each, breaking the single-consumer (leader-only) guarantee.
        //
        // Applied once here (before per-connection provisioning) so the mutated subscription id is shared by the
        // default project and every tenant project.
        if ((IsListener || IsDeadLetter) && !IsDeadLetter && ListenerScope == ListenerScope.CompetingConsumers)
        {
            Server.Subscription.Name =
                Server.Subscription.Name.WithAssignedNodeNumber(_transport.AssignedNodeNumber);
        }

        // Provision on the default/shared connection...
        await provisionAsync(logger, _transport.DefaultClients);

        // ...and on each tenant's own project (broker-per-tenant, GH-3306). Each tenant is an independent GCP
        // project, so the same logical topic/subscription must be created there too using the tenant's client.
        if (isTenantAware())
        {
            foreach (var tenant in _transport.Tenants)
            {
                await provisionAsync(logger, _transport.GetTenantClients(tenant));
            }
        }
    }

    private async Task provisionAsync(ILogger logger, PubsubClientSet clients)
    {
        if (clients.PublisherApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        var topicName = TopicNameFor(clients.ProjectId);

        try
        {
            await clients.PublisherApiClient.CreateTopicAsync(new Topic
            {
                TopicName = topicName,
                MessageRetentionDuration = Server.Topic.Options.MessageRetentionDuration
            });
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            logger.LogInformation("{Uri}: Google Cloud Platform Pub/Sub topic \"{Topic}\" already exists", Uri,
                topicName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub topic \"{Topic}\"",
                Uri, topicName);

            throw;
        }

        if (!IsListener && !IsDeadLetter)
        {
            return;
        }

        if (clients.SubscriberApiClient is null)
        {
            throw new WolverinePubsubTransportNotConnectedException();
        }

        var subscriptionName = SubscriptionNameFor(clients.ProjectId);

        try
        {
            var request = new Subscription
            {
                SubscriptionName = subscriptionName,
                TopicAsTopicName = topicName,
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

            await clients.SubscriberApiClient.CreateSubscriptionAsync(request);
        }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.AlreadyExists)
        {
            logger.LogInformation("{Uri}: Google Cloud Platform Pub/Sub subscription \"{Subscription}\" already exists",
                Uri, subscriptionName);
        }
        catch (Exception ex)
        {
            logger.LogError(ex,
                "{Uri}: Error trying to initialize Google Cloud Platform Pub/Sub subscription \"{Subscription}\" to topic \"{Topic}\"",
                Uri, subscriptionName, topicName);

            throw;
        }
    }

    private bool isTenantAware()
    {
        return _transport.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware;
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

        if (!isTenantAware())
        {
            return ValueTask.FromResult(buildListener(runtime, receiver, _transport.DefaultClients));
        }

        // Broker-per-tenant (GH-3306): the default listener consumes the shared project, and each tenant is
        // consumed over its own project via a dedicated listener that stamps the tenant id onto inbound envelopes
        // (mirrors the NATS / RabbitMQ / Azure Service Bus CompoundListener pattern). Pub/Sub acknowledges inline
        // in the streaming callback, so per-envelope completion never routes back through CompoundListener.
        var compound = new CompoundListener(Uri);
        compound.Inner.Add(buildListener(runtime, receiver, _transport.DefaultClients));

        foreach (var tenant in _transport.Tenants)
        {
            var tenantReceiver = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);
            compound.Inner.Add(buildListener(runtime, tenantReceiver, _transport.GetTenantClients(tenant)));
        }

        return ValueTask.FromResult<IListener>(compound);
    }

    private IListener buildListener(IWolverineRuntime runtime, IReceiver receiver, PubsubClientSet clients)
    {
        if (Mode == EndpointMode.Inline)
        {
            return new InlinePubsubListener(this, _transport, receiver, runtime, clients);
        }

        return new BatchedPubsubListener(this, _transport, receiver, runtime, clients);
    }

    public override DeadLetterStorageMode DeadLetterStorage => DeadLetterName.IsNotEmpty()
        ? DeadLetterStorageMode.Native
        : DeadLetterStorageMode.Durable;

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

    internal async Task SendMessageAsync(Envelope envelope, ILogger logger, PubsubClientSet? clients = null)
    {
        clients ??= _transport.DefaultClients;

        if (clients.PublisherApiClient is null)
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

        await clients.PublisherApiClient.PublishAsync(new PublishRequest
        {
            TopicAsTopicName = TopicNameFor(clients.ProjectId),
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

        if (isTenantAware())
        {
            // Broker-per-tenant (GH-3306): route outbound sends by Envelope.TenantId. Both the default-fallback
            // sender and every per-tenant sender MUST be the fire-and-forget InlinePubsubSender (not
            // BatchedSender + PubsubSenderProtocol): TenantedSender deliberately does not implement
            // ISenderRequiresCallback, so a BatchedSender underneath it would never have its outbox entries
            // deleted. See GH-2361.
            var defaultSender = new InlinePubsubSender(this, runtime, _transport.DefaultClients);
            var tenantedSender = new TenantedSender(Uri, _transport.TenantedIdBehavior, defaultSender);

            foreach (var tenant in _transport.Tenants)
            {
                var tenantSender = new InlinePubsubSender(this, runtime, _transport.GetTenantClients(tenant));
                tenantedSender.RegisterSender(tenant.TenantId, tenantSender);
            }

            return tenantedSender;
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