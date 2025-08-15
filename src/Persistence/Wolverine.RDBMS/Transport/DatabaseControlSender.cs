using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.RDBMS.Transport;

internal class DatabaseControlSender : ISender, IAsyncDisposable
{
    private readonly DatabaseControlEndpoint _endpoint;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly DatabaseControlTransport _transport;

    public DatabaseControlSender(DatabaseControlEndpoint endpoint, DatabaseControlTransport transport, ILogger logger,
        CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _transport = transport;
        Destination = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(sendMessageAsync, logger, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _retryBlock.DrainAsync();
        _retryBlock.Dispose();
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async Task<bool> PingAsync()
    {
        try
        {
            await using var conn = await _transport.Database.DataSource.OpenConnectionAsync();
            await conn.CloseAsync();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public async ValueTask SendAsync(Envelope envelope)
    {
        envelope.DeliverWithin = 10.Seconds();

        await _retryBlock.PostAsync(envelope);
    }

    private async Task sendMessageAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested || _transport.Database.HasDisposed)
        {
            return;
        }

        try
        {
            await _transport.Database.DataSource.CreateCommand(
                    $"insert into {_transport.TableName} (id, message_type, node_id, body, expires) values (@id, @messagetype, @node, @body, @expires)")
                .With("id", envelope.Id)
                .With("messagetype", envelope.MessageType!)
                .With("node", _endpoint.NodeId)
                .With("body", EnvelopeSerializer.Serialize(envelope))
                .With("expires", DateTimeOffset.UtcNow.AddSeconds(30)).ExecuteNonQueryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }
}