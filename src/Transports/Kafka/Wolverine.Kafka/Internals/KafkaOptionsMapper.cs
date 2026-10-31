using Dekaf;
using Dekaf.Admin;
using Dekaf.Consumer;
using JasperFx.Core;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// Translates Wolverine's curated Kafka option POCOs onto Dekaf's fluent client builders.
/// </summary>
internal static class KafkaOptionsMapper
{
    public static ConsumerBuilder<string, byte[]> ApplyOptions(this ConsumerBuilder<string, byte[]> builder,
        KafkaConsumerOptions options)
    {
        assertBootstrapServers(options);
        builder.WithBootstrapServers(options.BootstrapServers!);

        if (options.GroupId.IsNotEmpty())
        {
            builder.WithGroupId(options.GroupId);
        }

        if (options.GroupInstanceId.IsNotEmpty())
        {
            builder.WithGroupInstanceId(options.GroupInstanceId);
        }

        if (options.ClientId.IsNotEmpty())
        {
            builder.WithClientId(options.ClientId);
        }

        if (options.AutoOffsetReset.HasValue)
        {
            builder.WithAutoOffsetReset(options.AutoOffsetReset.Value);
        }

        if (options.IsolationLevel.HasValue)
        {
            builder.WithIsolationLevel(options.IsolationLevel.Value);
        }

        if (options.MaxPollRecords.HasValue)
        {
            builder.WithMaxPollRecords(options.MaxPollRecords.Value);
        }

        if (options.FetchMaxBytes.HasValue)
        {
            builder.WithFetchMaxBytes(options.FetchMaxBytes.Value);
        }

        if (options.MaxPartitionFetchBytes.HasValue)
        {
            builder.WithMaxPartitionFetchBytes(options.MaxPartitionFetchBytes.Value);
        }

        if (options.SessionTimeout.HasValue)
        {
            builder.WithSessionTimeout(options.SessionTimeout.Value);
        }

        if (options.HeartbeatInterval.HasValue)
        {
            builder.WithHeartbeatInterval(options.HeartbeatInterval.Value);
        }

        // Commit knobs. EnableAutoCommit=false means Wolverine commits offsets itself; anything else
        // leaves the background committer on (the Kafka client default). EnableAutoOffsetStore=false
        // limits the background committer to explicitly stored offsets. When storage is hands-off,
        // OffsetStoreTiming.AfterProcessing makes an offset committable only once the listener asks for
        // the next record. Inline listeners, and durable listeners receiving one record at a time, wait for
        // the handler or the inbox before that, so a crash redelivers their in-flight record instead of
        // losing it. A durable micro-batch asks for its later records before the batch reaches the inbox.
        if (options.EnableAutoCommit == false)
        {
            builder.WithOffsetCommitMode(OffsetCommitMode.Manual);
        }

        if (options.EnableAutoOffsetStore == false)
        {
            builder.WithAutoOffsetStore(false);
        }
        else
        {
            builder.WithOffsetStoreTiming(OffsetStoreTiming.AfterProcessing);
        }

        if (options.AutoCommitInterval.HasValue)
        {
            builder.WithAutoCommitInterval(options.AutoCommitInterval.Value);
        }

        applySecurity(options, () => builder.UseTls(),
            (u, p) => builder.WithSaslPlain(u, p),
            (u, p) => builder.WithSaslScramSha256(u, p),
            (u, p) => builder.WithSaslScramSha512(u, p));

        return builder;
    }

    public static ProducerBuilder<string, byte[]> ApplyOptions(this ProducerBuilder<string, byte[]> builder,
        KafkaProducerOptions options)
    {
        assertBootstrapServers(options);
        builder.WithBootstrapServers(options.BootstrapServers!);

        if (options.ClientId.IsNotEmpty())
        {
            builder.WithClientId(options.ClientId);
        }

        if (options.EnableIdempotence.HasValue)
        {
            builder.WithIdempotence(options.EnableIdempotence.Value);
        }

        if (options.Acks.HasValue)
        {
            builder.WithAcks(options.Acks.Value);
        }

        if (options.Linger.HasValue)
        {
            builder.WithLinger(options.Linger.Value);
        }

        if (options.BatchSize.HasValue)
        {
            builder.WithBatchSize(options.BatchSize.Value);
        }

        applySecurity(options, () => builder.UseTls(),
            (u, p) => builder.WithSaslPlain(u, p),
            (u, p) => builder.WithSaslScramSha256(u, p),
            (u, p) => builder.WithSaslScramSha512(u, p));

        return builder;
    }

    public static AdminClientBuilder ApplyOptions(this AdminClientBuilder builder, KafkaAdminClientOptions options)
    {
        assertBootstrapServers(options);
        builder.WithBootstrapServers(options.BootstrapServers!);

        if (options.ClientId.IsNotEmpty())
        {
            builder.WithClientId(options.ClientId);
        }

        applySecurity(options, () => builder.UseTls(),
            (u, p) => builder.WithSaslPlain(u, p),
            (u, p) => builder.WithSaslScramSha256(u, p),
            (u, p) => builder.WithSaslScramSha512(u, p));

        return builder;
    }

    private static void assertBootstrapServers(KafkaClientOptions options)
    {
        if (options.BootstrapServers.IsEmpty())
        {
            throw new InvalidOperationException(
                "No Kafka bootstrap servers are configured. Supply them through UseKafka(bootstrapServers) or a named connection.");
        }
    }

    private static void applySecurity(KafkaClientOptions options, Action useTls,
        Action<string, string> saslPlain, Action<string, string> saslScram256, Action<string, string> saslScram512)
    {
        if (options.SecurityProtocol is KafkaSecurityProtocol.Ssl or KafkaSecurityProtocol.SaslSsl)
        {
            useTls();
        }

        if (options.SaslMechanism == null)
        {
            return;
        }

        var username = options.SaslUsername ?? string.Empty;
        var password = options.SaslPassword ?? string.Empty;

        switch (options.SaslMechanism)
        {
            case KafkaSaslMechanism.Plain:
                saslPlain(username, password);
                break;
            case KafkaSaslMechanism.ScramSha256:
                saslScram256(username, password);
                break;
            case KafkaSaslMechanism.ScramSha512:
                saslScram512(username, password);
                break;
        }
    }
}
