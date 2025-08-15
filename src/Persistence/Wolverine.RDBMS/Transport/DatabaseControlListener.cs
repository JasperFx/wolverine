using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.RDBMS.Polling;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.Transport;

internal class DatabaseControlListener : IListener
{
    private readonly IReceiver _receiver;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly DatabaseControlTransport _transport;
    private readonly Task _receivingLoop;
    private readonly CancellationTokenSource _cancellation;

    public DatabaseControlListener(DatabaseControlTransport transport, DatabaseControlEndpoint endpoint,
        IReceiver receiver, ILogger<DatabaseControlListener> logger, CancellationToken cancellationToken)
    {
        _transport = transport;
        _receiver = receiver;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _receivingLoop = Task.Run(async () =>
        {
            await Task.Delay(Random.Shared.Next(100, 1000).Milliseconds());

            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    var batch = new DatabaseOperationBatch(_transport.Database,
                    [
                        new DeleteExpiredMessages(_transport, DateTimeOffset.UtcNow),
                            new PollDatabaseControlQueue(_transport, _receiver, this)
                    ]);
                    await receiver.ReceivedAsync(this, new Envelope(batch));
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error trying to poll for messages from the database control queue");
                }

                await Task.Delay(1.Seconds());
            }
        }, _cancellation.Token);


        Address = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(deleteEnvelopeAsync, logger, cancellationToken);
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _retryBlock.PostAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync();


#else
        _cancellation.Cancel();
#endif
        _receivingLoop.SafeDispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
#if NET8_0_OR_GREATER
        await _cancellation.CancelAsync();


        #else
        _cancellation.Cancel();
#endif
        if (_receivingLoop != null)
        {
            await _receivingLoop;
            _receivingLoop.Dispose();
        }
    }

    private Task deleteEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (_transport.Database.HasDisposed) return Task.CompletedTask;

        return _transport.Database.DataSource.CreateCommand($"delete from {_transport.TableName} where id = @id")
            .With("id", envelope.Id)
            .ExecuteNonQueryAsync(cancellationToken);
    }
}