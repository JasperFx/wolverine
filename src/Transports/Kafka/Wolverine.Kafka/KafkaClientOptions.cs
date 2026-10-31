using Dekaf.Consumer;
using Dekaf.Producer;
using Dekaf.Security.Sasl;
using IsolationLevel = Dekaf.Protocol.Messages.IsolationLevel;

namespace Wolverine.Kafka;

/// <summary>
/// Wolverine-owned, mutable connection settings shared by Kafka producers, consumers, and admin
/// clients. Names and types follow Dekaf's client options. Translated onto the Dekaf client builders
/// when clients are created; anything beyond this curated surface is reachable through the
/// ConfigureProducerBuilders / ConfigureConsumerBuilders / ConfigureAdminClientBuilders escape hatches.
/// </summary>
public abstract class KafkaClientOptions
{
    /// <summary>
    /// CSV of host:port pairs, e.g. "broker1:9092,broker2:9092"
    /// </summary>
    public string? BootstrapServers { get; set; }

    public string? ClientId { get; set; }

    /// <summary>
    /// Connect over TLS with the default certificate validation. Custom certificates and mutual TLS are
    /// configured on the client builders
    /// </summary>
    public bool? UseTls { get; set; }

    /// <summary>
    /// The SASL mechanism, authenticating with <see cref="SaslUsername" /> and <see cref="SaslPassword" />.
    /// Only <see cref="Dekaf.Security.Sasl.SaslMechanism.Plain" />, <see cref="Dekaf.Security.Sasl.SaslMechanism.ScramSha256" />
    /// and <see cref="Dekaf.Security.Sasl.SaslMechanism.ScramSha512" /> are configured from these options; the others
    /// need settings of their own on the client builders
    /// </summary>
    public SaslMechanism? SaslMechanism { get; set; }

    public string? SaslUsername { get; set; }
    public string? SaslPassword { get; set; }

    // GSSAPI, OAuth bearer and AWS MSK IAM need more than a username and password
    internal void AssertSaslSupported(string owner)
    {
        if (SaslMechanism is Dekaf.Security.Sasl.SaslMechanism.Gssapi or Dekaf.Security.Sasl.SaslMechanism.OAuthBearer
            or Dekaf.Security.Sasl.SaslMechanism.AwsMskIam)
        {
            throw new InvalidOperationException(
                $"The Kafka options for {owner} set SaslMechanism = {SaslMechanism}, which needs settings beyond a username and password. Leave SaslMechanism unset and configure it on the client builders instead, e.g. ConfigureConsumerBuilders(b => b.WithGssapi(...)), WithOAuthBearer(...) or WithAwsMskIam(...)");
        }
    }

    internal void InheritClientDefaultsFrom(KafkaClientOptions parent)
    {
        BootstrapServers ??= parent.BootstrapServers;
        ClientId ??= parent.ClientId;
        UseTls ??= parent.UseTls;
        SaslMechanism ??= parent.SaslMechanism;
        SaslUsername ??= parent.SaslUsername;
        SaslPassword ??= parent.SaslPassword;
    }
}

public class KafkaConsumerOptions : KafkaClientOptions
{
    public string? GroupId { get; set; }

    /// <summary>
    /// Static group membership id (group.instance.id)
    /// </summary>
    public string? GroupInstanceId { get; set; }

    /// <summary>
    /// Where a consumer group with no committed offset begins reading. <see cref="Dekaf.Consumer.AutoOffsetReset.ByDuration" />
    /// also needs <see cref="AutoOffsetResetDuration" />
    /// </summary>
    public AutoOffsetReset? AutoOffsetReset { get; set; }

    /// <summary>
    /// How far back a consumer group with no committed offset begins reading, when <see cref="AutoOffsetReset" />
    /// is <see cref="Dekaf.Consumer.AutoOffsetReset.ByDuration" />
    /// </summary>
    public TimeSpan? AutoOffsetResetDuration { get; set; }

    /// <summary>
    /// <see cref="Dekaf.Consumer.OffsetCommitMode.Manual" /> leaves commits to Wolverine;
    /// <see cref="Dekaf.Consumer.OffsetCommitMode.Auto" /> lets the client's background committer flush offsets on its
    /// own cadence. Unset, the listener's <see cref="CommitMode" /> chooses
    /// </summary>
    public OffsetCommitMode? OffsetCommitMode { get; set; }

    /// <summary>
    /// When false, only offsets explicitly stored after successful processing are eligible for the
    /// background committer's flush
    /// </summary>
    public bool? EnableAutoOffsetStore { get; set; }

    public TimeSpan? AutoCommitInterval { get; set; }

    public IsolationLevel? IsolationLevel { get; set; }

    public int? MaxPollRecords { get; set; }
    public int? FetchMaxBytes { get; set; }
    public int? MaxPartitionFetchBytes { get; set; }
    public TimeSpan? SessionTimeout { get; set; }
    public TimeSpan? HeartbeatInterval { get; set; }

    public KafkaConsumerOptions Clone()
    {
        return (KafkaConsumerOptions)MemberwiseClone();
    }

    internal void InheritDefaultsFrom(KafkaConsumerOptions parent)
    {
        InheritClientDefaultsFrom(parent);
        GroupId ??= parent.GroupId;
        GroupInstanceId ??= parent.GroupInstanceId;
        AutoOffsetReset ??= parent.AutoOffsetReset;
        AutoOffsetResetDuration ??= parent.AutoOffsetResetDuration;
        OffsetCommitMode ??= parent.OffsetCommitMode;
        EnableAutoOffsetStore ??= parent.EnableAutoOffsetStore;
        AutoCommitInterval ??= parent.AutoCommitInterval;
        IsolationLevel ??= parent.IsolationLevel;
        MaxPollRecords ??= parent.MaxPollRecords;
        FetchMaxBytes ??= parent.FetchMaxBytes;
        MaxPartitionFetchBytes ??= parent.MaxPartitionFetchBytes;
        SessionTimeout ??= parent.SessionTimeout;
        HeartbeatInterval ??= parent.HeartbeatInterval;
    }
}

public class KafkaProducerOptions : KafkaClientOptions
{
    /// <summary>
    /// The idempotent producer (implies acks=all and bounded in-flight requests) keeps producer-side
    /// retries from writing duplicates to the broker. Unset leaves Dekaf's default, which is enabled
    /// </summary>
    public bool? EnableIdempotence { get; set; }

    /// <summary>
    /// Anything other than <see cref="Dekaf.Producer.Acks.All" /> needs <see cref="EnableIdempotence" /> set to false,
    /// since an idempotent producer waits for every in-sync replica
    /// </summary>
    public Acks? Acks { get; set; }

    // Dekaf refuses to build an idempotent producer with Acks.None and quietly raises other acks to All. Producers
    // are built on first send, so this fails at startup rather than on every send
    internal void AssertAcksAllowed(string owner)
    {
        if (Acks is { } acks && acks != Dekaf.Producer.Acks.All && EnableIdempotence != false)
        {
            throw new InvalidOperationException(
                $"The Kafka producer options for {owner} set Acks = {acks}, which an idempotent producer cannot use. Dekaf producers are idempotent by default, so also set EnableIdempotence = false, or use Acks.All");
        }
    }

    public TimeSpan? Linger { get; set; }
    public int? BatchSize { get; set; }

    public KafkaProducerOptions Clone()
    {
        return (KafkaProducerOptions)MemberwiseClone();
    }
}

public class KafkaAdminClientOptions : KafkaClientOptions
{
    public KafkaAdminClientOptions Clone()
    {
        return (KafkaAdminClientOptions)MemberwiseClone();
    }
}
