using Dekaf.Diagnostics;
using Dekaf.Producer;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// A Dekaf producer built on first use and kept until disposal. Dekaf producers are built and initialized
/// asynchronously, but the senders that own one are constructed on synchronous paths.
/// </summary>
internal sealed class LazyKafkaProducer : IAsyncDisposable, IKafkaClientHealthSource
{
    private readonly KafkaTransport _transport;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IKafkaProducer<string, byte[]>? _producer;
    private volatile bool _failedToConnect;

    public LazyKafkaProducer(KafkaTransport transport, KafkaProducerOptions config)
    {
        _transport = transport;
        Config = config;
        transport.TrackClient(this);
    }

    /// <summary>
    /// A producer for <paramref name="topic" /> on <paramref name="transport" />. On the topic's own transport it
    /// honors the topic-level producer overrides; a tenant's transport (GH-3303) uses that cluster's options.
    /// </summary>
    public static LazyKafkaProducer For(KafkaTopic topic, KafkaTransport transport)
    {
        var config = ReferenceEquals(transport, topic.Parent)
            ? topic.GetEffectiveProducerConfig()
            : transport.ProducerConfig;

        return new LazyKafkaProducer(transport, config);
    }

    public KafkaProducerOptions Config { get; }

    public KafkaClientStatus? Status => (_producer as IKafkaClientStatusProvider)?.GetStatus();

    public bool FailedToConnect => _failedToConnect;

    public async ValueTask<IKafkaProducer<string, byte[]>> GetAsync()
    {
        if (_producer != null) return _producer;

        await _lock.WaitAsync();
        try
        {
            if (_producer == null)
            {
                try
                {
                    _producer = await _transport.CreateProducerAsync(Config);
                    _failedToConnect = false;
                }
                catch (Exception)
                {
                    _failedToConnect = true;
                    throw;
                }
            }

            return _producer;
        }
        finally
        {
            _lock.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.ForgetClient(this);

        var producer = _producer;
        _lock.Dispose();
        if (producer == null)
        {
            return;
        }

        // Dekaf drains anything still queued within its close timeout before tearing the producer down
        try
        {
            await producer.DisposeAsync();
        }
        catch (Exception)
        {
            // already gone, nothing more to do
        }
    }
}
