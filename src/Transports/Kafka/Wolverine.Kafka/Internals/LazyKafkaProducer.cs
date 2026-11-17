using Dekaf.Diagnostics;
using Dekaf.Producer;

namespace Wolverine.Kafka.Internals;

/// <summary>
/// A Dekaf producer built on first use and kept until its <see cref="KafkaProducerPool" /> is disposed. Dekaf
/// producers are built and initialized asynchronously, but the senders that borrow one are constructed on
/// synchronous paths.
/// </summary>
internal sealed class LazyKafkaProducer : IAsyncDisposable, IKafkaClientHealthSource
{
    private readonly KafkaTransport _transport;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile IKafkaProducer<string, byte[]>? _producer;
    private volatile bool _failedToConnect;
    private bool _disposed;

    public LazyKafkaProducer(KafkaTransport transport, KafkaProducerOptions config)
    {
        _transport = transport;
        Config = config;
        transport.TrackClient(this);
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
            ObjectDisposedException.ThrowIf(_disposed, this);

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

        // Waits out a producer still being built, so it is disposed too. Callers that queue up after this see the
        // disposed flag rather than building another
        IKafkaProducer<string, byte[]>? producer;
        await _lock.WaitAsync();
        try
        {
            if (_disposed) return;
            _disposed = true;
            producer = _producer;
        }
        finally
        {
            _lock.Release();
        }

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
