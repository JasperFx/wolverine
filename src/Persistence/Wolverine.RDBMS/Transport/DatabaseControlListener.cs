using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Transports;
using Wolverine.Util.Dataflow;

namespace Wolverine.RDBMS.Transport;

internal class DatabaseControlListener : IListener
{
    private readonly DatabaseControlTransport _transport;
    private readonly DatabaseControlEndpoint _endpoint;
    private readonly IReceiver _receiver;
    private readonly ILogger<DatabaseControlListener> _logger;
    private readonly Timer _timer;
    private readonly RetryBlock<Envelope> _retryBlock;

    public DatabaseControlListener(DatabaseControlTransport transport, DatabaseControlEndpoint endpoint,
        IReceiver receiver, ILogger<DatabaseControlListener> logger, CancellationToken cancellationToken)
    {
        _transport = transport;
        _endpoint = endpoint;
        _receiver = receiver;
        _logger = logger;
        _timer = new Timer(fireTimer, this, new Random().Next(100, 1000).Milliseconds(), 1.Seconds());

        Address = _endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(deleteEnvelopeAsync, logger, cancellationToken);
    }
    
    private void fireTimer(object status)
    {
        _transport.Database.EnqueueAsync(new PollDatabaseControlQueue(_transport, _receiver, this)).GetAwaiter().GetResult();
    }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _retryBlock.PostAsync(envelope);
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

    public ValueTask DeferAsync(Envelope envelope)
    {
        return new ValueTask();
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
}