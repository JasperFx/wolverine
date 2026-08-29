using System.Text.RegularExpressions;
using Dekaf.Admin;
using Dekaf.Errors;
using Dekaf.Protocol;
using Microsoft.Extensions.Logging;
using Wolverine.Configuration;
using Wolverine.Kafka.Internals;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;

namespace Wolverine.Kafka;

/// <summary>
/// Represents a single Wolverine listener that subscribes to multiple Kafka topics
/// using one Kafka consumer. This reduces consumer group rebalances and startup time
/// when many topics share the same logical workload.
/// </summary>
public class KafkaTopicGroup : KafkaTopic, IBrokerEndpoint
{
    /// <summary>
    /// The Kafka topic names this endpoint subscribes to
    /// </summary>
    public string[] TopicNames { get; }

    public KafkaTopicGroup(KafkaTransport parent, string[] topicNames, EndpointRole role)
        : base(parent, SanitizeGroupName(string.Join("_", topicNames)), role)
    {
        TopicNames = topicNames;
        EndpointName = string.Join("_", topicNames);
    }

    internal static string SanitizeGroupName(string name)
    {
        // Replace any characters that are not valid in a URI path segment
        return Regex.Replace(name, @"[^a-zA-Z0-9_\-.]", "_");
    }

    public override bool AutoStartSendingAgent() => false;

    public override async ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        EnvelopeMapper ??= BuildMapper(runtime);

        var config = GetEffectiveConsumerConfig();

        ApplyHotTailConfig(config, runtime);

        // Wire the Kafka client for the configured commit strategy (GH-3150).
        KafkaOffsetCommitter.ApplyTo(config, CommitMode);

        // GH-3454: consume exceptions feed the tracker, which the listener surfaces through
        // IReportConnectionState
        var tracker = new KafkaConnectionStateTracker();
        var listener = new KafkaListener(this, config,
            Parent.CreateConsumer(config), TopicNames, receiver,
            runtime.LoggerFactory.CreateLogger<KafkaListener>(),
            runtime.DurabilitySettings.DrainTimeout, connectionState: tracker);

        // Broker-per-tenant (GH-3303): mirror the single-topic listener treatment — a shared listener on the
        // default cluster plus one per-tenant listener on each tenant cluster, stamping the tenant id inbound.
        if (Parent.Tenants.Any() && TenancyBehavior == TenancyBehavior.TenantAware)
        {
            var compound = new CompoundListener(Uri);
            compound.Inner.Add(listener);

            foreach (var tenant in Parent.Tenants)
            {
                var tenantConfig = cloneConsumerConfigForTenant(config, tenant);
                var tenantReceiver = new ReceiverWithRules(receiver, [new TenantIdRule(tenant.TenantId)]);
                var tenantTracker = new KafkaConnectionStateTracker();
                var tenantListener = new KafkaListener(this, tenantConfig,
                    tenant.Transport.CreateConsumer(tenantConfig), TopicNames, tenantReceiver,
                    runtime.LoggerFactory.CreateLogger<KafkaListener>(),
                    runtime.DurabilitySettings.DrainTimeout, tenant.Transport, tenantTracker);
                compound.Inner.Add(tenantListener);
            }

            return compound;
        }

        return listener;
    }

    // GH-4510. The same structural answer the Azure Service Bus subscription now gives: a sticky handler
    // binding must not make a listen-only endpoint a local send target, or building its sender takes host
    // startup down from inside PrepopulateRoutingCache.
    protected internal override bool supportsSending => false;

    protected override ISender CreateSender(IWolverineRuntime runtime)
    {
        throw new NotSupportedException("KafkaTopicGroup is a listen-only endpoint. Use KafkaTopic for publishing.");
    }

    public override bool TryBuildDeadLetterSender(IWolverineRuntime runtime, out ISender? deadLetterSender)
    {
        if (NativeDeadLetterQueueEnabled)
        {
            var dlqTopic = Parent.Topics[Parent.DeadLetterQueueTopicName];
            dlqTopic.EnvelopeMapper ??= dlqTopic.BuildMapper(runtime);
            deadLetterSender = Parent.TrackOwnedSender(new InlineKafkaSender(dlqTopic, fixedDestination: true));
            return true;
        }

        deadLetterSender = default;
        return false;
    }

    // Re-implement IBrokerEndpoint for multi-topic support

    new public async ValueTask<bool> CheckAsync()
    {
        if (Parent.Usage == KafkaUsage.ConsumeOnly) return true;

        try
        {
            await using var client = await Parent.CreateProducerAsync(Parent.ProducerConfig);
            foreach (var topicName in TopicNames)
            {
                await client.ProduceAsync(topicName, "ping", "ping"u8.ToArray(), CancellationToken.None);
            }

            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    new public async ValueTask TeardownAsync(ILogger logger)
    {
        if (IsExternallyOwned) return;

        await using var adminClient = Parent.CreateAdminClient();
        await adminClient.DeleteTopicsAsync(TopicNames, new DeleteTopicsOptions(), CancellationToken.None);
    }

    /// <summary>
    /// Optional action to configure the topic specification for each topic created by this group.
    /// The string parameter is the topic name. Applied before topic creation unless <see cref="CreateTopicFunc"/> is overridden.
    /// </summary>
    public Action<string, KafkaTopicSpecification>? SpecificationConfig { get; set; }

    /// <summary>
    /// Optional override for topic creation logic. Receives the admin client and topic name.
    /// Defaults to creating the topic using <see cref="SpecificationConfig"/> if set.
    /// </summary>
    public new Func<IAdminClient, string, Task>? CreateTopicFunc { get; set; }

    new public async ValueTask SetupAsync(ILogger logger)
    {
        if (IsExternallyOwned) return;

        await using var adminClient = Parent.CreateAdminClient();
        await SetupOnAsync(adminClient, logger);
    }

    /// <summary>
    /// Create every topic in this group on the supplied admin client. Split out from <see cref="SetupAsync"/>
    /// so the same multi-topic creation logic can be applied against a tenant cluster (broker-per-tenant, GH-3303).
    /// </summary>
    internal new async ValueTask SetupOnAsync(IAdminClient adminClient, ILogger logger)
    {
        if (IsExternallyOwned) return;

        foreach (var topicName in TopicNames)
        {
            try
            {
                if (CreateTopicFunc != null)
                {
                    await CreateTopicFunc(adminClient, topicName);
                }
                else
                {
                    var spec = new KafkaTopicSpecification { Name = topicName };
                    SpecificationConfig?.Invoke(topicName, spec);
                    await adminClient.CreateTopicsAsync([spec.ToNewTopic()], new CreateTopicsOptions(),
                        CancellationToken.None);
                }

                logger.LogInformation("Created Kafka topic {Topic}", topicName);
            }
            catch (KafkaException e) when (e.ErrorCode == ErrorCode.TopicAlreadyExists)
            {
            }
        }
    }

    /// <summary>
    /// Called during transport startup. When AutoProvision is on for the parent
    /// transport, ensure every Kafka topic in this group exists on the broker
    /// before the listener subscribes. Without this, the group listener's
    /// consumer raises "Subscribed topic not available" on the first consume.
    /// Overrides the base KafkaTopic.InitializeAsync so the group's multi-topic
    /// SetupAsync is invoked (not the single-topic base version).
    /// Groups marked <see cref="KafkaTopic.IsExternallyOwned"/> are skipped so
    /// externally-managed topics don't fail startup when the calling identity
    /// lacks CreateTopics ACLs.
    /// See https://github.com/JasperFx/wolverine/issues/2537.
    /// </summary>
    public override async ValueTask InitializeAsync(ILogger logger)
    {
        if (Parent.AutoProvision && !IsExternallyOwned)
        {
            await SetupAsync(logger);
        }
    }
}
