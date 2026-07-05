using Confluent.Kafka;
using JasperFx.Core;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Represents a single Wolverine tenant that is served by its own dedicated Kafka cluster/connection while
/// sharing the topic topology declared on the parent transport. A Kafka "connection" is config-only (three
/// Confluent config bags plus builder callbacks), so — mirroring the RabbitMQ tenant model — each tenant owns
/// a child <see cref="KafkaTransport"/> whose config is cloned from the parent and then re-pointed at the
/// tenant's own <c>BootstrapServers</c>. Outbound routing is by <see cref="Envelope.TenantId"/> via the
/// framework's <see cref="Wolverine.Transports.Sending.TenantedSender"/>; inbound the tenant's own listener
/// stamps the tenant id.
/// </summary>
internal class KafkaTenant
{
    public KafkaTenant(string tenantId)
    {
        TenantId = tenantId ?? throw new ArgumentNullException(nameof(tenantId));
        Transport = new KafkaTransport();
    }

    public string TenantId { get; }

    /// <summary>
    /// The child transport that provides this tenant's dedicated producer/consumer/admin clients. Its config
    /// is a clone of the parent's, re-pointed at the tenant's own cluster during <see cref="Compile"/>.
    /// </summary>
    public KafkaTransport Transport { get; }

    /// <summary>
    /// The tenant cluster's bootstrap servers. Applied over the cloned parent config in <see cref="Compile"/>.
    /// Null when the tenant is configured entirely through <see cref="Configure"/>.
    /// </summary>
    public string? BootstrapServers { get; set; }

    /// <summary>
    /// Optional full configuration hook (auth / SASL / SSL / advanced client options) run against the tenant's
    /// child transport after the parent config has been cloned. Lets a tenant override anything the parent set.
    /// </summary>
    public Action<KafkaTransportExpression>? Configure { get; set; }

    /// <summary>
    /// Clone the parent's three Confluent config bags, the three <c>Configure*Builders</c> callbacks, and the
    /// DLQ topic name onto this tenant's child transport, then re-point it at the tenant's own cluster. The
    /// child-transport approach inherits EOS / idempotence / static-membership flags and gets
    /// <c>CreateProducer</c>/<c>CreateConsumer</c>/<c>CreateAdminClient</c> for free.
    ///
    /// NOTE (consumer group id): each tenant is a <em>separate</em> Kafka cluster, so offsets live in that
    /// cluster and the group id is intentionally cloned unchanged from the parent — it is <b>not</b> suffixed
    /// per tenant. Suffixing would be wrong here (unlike NATS subject prefixing on a shared connection).
    /// </summary>
    public KafkaTransport Compile(KafkaTransport parent, WolverineOptions options)
    {
        copyInto(parent.ProducerConfig, Transport.ProducerConfig);
        copyInto(parent.ConsumerConfig, Transport.ConsumerConfig);
        copyInto(parent.AdminClientConfig, Transport.AdminClientConfig);

        Transport.ConfigureProducerBuilders = parent.ConfigureProducerBuilders;
        Transport.ConfigureConsumerBuilders = parent.ConfigureConsumerBuilders;
        Transport.ConfigureAdminClientBuilders = parent.ConfigureAdminClientBuilders;

        Transport.DeadLetterQueueTopicName = parent.DeadLetterQueueTopicName;
        Transport.Usage = parent.Usage;
        Transport.AutoProvision = parent.AutoProvision;

        if (BootstrapServers.IsNotEmpty())
        {
            Transport.ProducerConfig.BootstrapServers = BootstrapServers;
            Transport.ConsumerConfig.BootstrapServers = BootstrapServers;
            Transport.AdminClientConfig.BootstrapServers = BootstrapServers;
        }

        if (Configure != null)
        {
            var expression = new KafkaTransportExpression(Transport, options);
            Configure(expression);
        }

        // Same cluster-independent identities the parent resolves in tryBuildSystemEndpoints. Crucially the
        // GroupId is inherited, NOT suffixed — offsets are per-cluster so tenant consumers can share it.
        Transport.ConsumerConfig.GroupId ??= parent.ConsumerConfig.GroupId ?? options.ServiceName;
        Transport.ProducerConfig.ClientId ??= parent.ProducerConfig.ClientId ?? options.ServiceName;

        return Transport;
    }

    // Confluent's Producer/Consumer/AdminClient configs all derive from ClientConfig, which is an
    // IEnumerable<KeyValuePair<string,string>> over its property bag with a public Set(key, val). Copying the
    // raw property bag preserves every configured option (SASL/SSL/idempotence/static-membership/…) generically.
    private static void copyInto(ClientConfig source, ClientConfig target)
    {
        foreach (var pair in source)
        {
            target.Set(pair.Key, pair.Value);
        }
    }
}
