using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.Oracle.Transport;

/// <summary>
/// Oracle clone of <see cref="Wolverine.RDBMS.Transport.DatabaseControlSender"/> — uses
/// <c>:</c> placeholders and routes Guid values through Weasel.Oracle.With(...) which
/// converts them to byte[] for the RAW(16) id columns. See #2622.
/// </summary>
internal class OracleControlSender : ISender, IAsyncDisposable
{
    private readonly OracleControlEndpoint _endpoint;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly OracleControlTransport _transport;

    public OracleControlSender(OracleControlEndpoint endpoint, OracleControlTransport transport, ILogger logger,
        CancellationToken cancellationToken)
    {
        _endpoint = endpoint;
        _transport = transport;
        Destination = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(sendMessageAsync, logger, cancellationToken);
    }

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination { get; }

    public async ValueTask DisposeAsync()
    {
        await _retryBlock.DrainAsync();
        _retryBlock.Dispose();
    }

    public async Task<bool> PingAsync()
    {
        try
        {
            await using var conn = await _transport.Database.OracleDataSource.OpenConnectionAsync();
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
            await using var conn = await _transport.Database.OracleDataSource.OpenConnectionAsync(cancellationToken);
            try
            {
                await using var cmd = conn.CreateCommand(
                    $"INSERT INTO {_transport.TableName.QualifiedName} (id, message_type, node_id, body, expires) " +
                    "VALUES (:id, :messagetype, :node, :body, :expires)");

                cmd.With("id", envelope.Id);
                cmd.With("messagetype", envelope.MessageType!);
                cmd.With("node", _endpoint.NodeId);
                cmd.Parameters.Add(new OracleParameter("body", OracleDbType.Blob)
                {
                    Value = EnvelopeSerializer.Serialize(envelope)
                });
                cmd.With("expires", DateTimeOffset.UtcNow.AddSeconds(30));

                await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await conn.CloseAsync();
            }
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
