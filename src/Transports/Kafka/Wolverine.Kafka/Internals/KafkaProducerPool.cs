using System.Collections.Concurrent;
using Dekaf.Producer;
using Dekaf.Security.Sasl;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// The producers for one Kafka cluster. Everything that produces with the same options (topic senders, dead letter
/// and retry-tier moves) borrows one producer, and only the pool disposes it.
/// </summary>
internal sealed class KafkaProducerPool : IAsyncDisposable
{
    private readonly KafkaTransport _transport;
    private readonly ConcurrentDictionary<Key, LazyKafkaProducer> _producers = new();

    public KafkaProducerPool(KafkaTransport transport) => _transport = transport;

    internal int Count => _producers.Count;

    public LazyKafkaProducer For(KafkaProducerOptions options)
    {
        return _producers.GetOrAdd(Key.Of(options),
            static (_, state) => new LazyKafkaProducer(state.Transport, state.Options), (Transport: _transport, Options: options));
    }

    public async ValueTask DisposeAsync()
    {
        var producers = _producers.Values.ToArray();
        _producers.Clear();

        // Each drain is bounded, and they are independent
        await Task.WhenAll(producers.Select(x => x.DisposeAsync().AsTask()));
    }

    // Every option that reaches the producer builder, so two producers only share when they would be built the same
    private readonly record struct Key(
        string? BootstrapServers,
        string? ClientId,
        bool? UseTls,
        SaslMechanism? SaslMechanism,
        string? SaslUsername,
        string? SaslPassword,
        bool? EnableIdempotence,
        Acks? Acks,
        TimeSpan? Linger,
        int? BatchSize)
    {
        public static Key Of(KafkaProducerOptions options) => new(options.BootstrapServers, options.ClientId,
            options.UseTls, options.SaslMechanism, options.SaslUsername, options.SaslPassword,
            options.EnableIdempotence, options.Acks, options.Linger, options.BatchSize);
    }
}
