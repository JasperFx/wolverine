using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.Transport;

internal class DatabaseControlListener : IListener
{
    private readonly IReceiver _receiver;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly Timer _timer;
    private readonly DatabaseControlTransport _transport;

    public DatabaseControlListener(DatabaseControlTransport transport, DatabaseControlEndpoint endpoint,
        IReceiver receiver, ILogger<DatabaseControlListener> logger, CancellationToken cancellationToken)
    {
        _transport = transport;
        _receiver = receiver;
        
        _timer = new Timer(
            fireTimer!, 
            this, 
            
            // Purposely using a random time period to start the timer
            // to keep each node starting up at the same time from hammering
            // the database table at the exact same time
            new Random().Next(100, 1000).Milliseconds(), 
            1.Seconds());

        Address = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(deleteEnvelopeAsync, logger, cancellationToken);
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _retryBlock.PostAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        return _timer.DisposeAsync();
    }

    public Uri Address { get; }

    public ValueTask StopAsync()
    {
        return _timer.DisposeAsync();
    }

    private void fireTimer(object status)
    {
        _transport.Database.Enqueue(new DeleteExpiredMessages(_transport, DateTimeOffset.UtcNow));
        _transport.Database.Enqueue(new PollDatabaseControlQueue(_transport, _receiver, this));
    }

    private async Task deleteEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        await using var conn = _transport.Database.CreateConnection();
        await conn.OpenAsync(cancellationToken);

        await conn.CreateCommand($"delete from {_transport.TableName} where id = @id")
            .With("id", envelope.Id)
            .ExecuteNonQueryAsync(cancellationToken);

        await conn.CloseAsync();
    }
}