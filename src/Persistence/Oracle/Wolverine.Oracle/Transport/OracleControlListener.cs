using JasperFx.Blocks;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Oracle.ManagedDataAccess.Client;
using Weasel.Oracle;
using Wolverine.Configuration;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Oracle.Transport;

/// <summary>
/// Oracle clone of <see cref="Wolverine.RDBMS.Transport.DatabaseControlListener"/> - polls the
/// Oracle control queue directly (rather than going through DatabaseOperationBatch which uses
/// <c>@param</c> placeholders and Guid-as-DbParameter values that Oracle rejects). Cleans up
/// expired messages on each tick. See #2622.
/// </summary>
internal class OracleControlListener : IListener
{
    private readonly CancellationTokenSource _cancellation;
    private readonly IReceiver _receiver;
    private readonly Task _receivingLoop;
    private readonly RetryBlock<Envelope> _retryBlock;
    private readonly OracleControlTransport _transport;
    private readonly ILogger _logger;

    public OracleControlListener(OracleControlTransport transport, OracleControlEndpoint endpoint, IReceiver receiver,
        ILogger<OracleControlListener> logger, CancellationToken cancellationToken)
    {
        _transport = transport;
        _receiver = receiver;
        _logger = logger;

        _cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _receivingLoop = Task.Run(async () =>
        {
            // Stagger startup so concurrent nodes don't hammer the table at the same instant.
            await Task.Delay(Random.Shared.Next(100, 1000).Milliseconds(), _cancellation.Token);

            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    await pollOnceAsync(_cancellation.Token);
                }
                catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception e)
                {
                    _logger.LogError(e, "Error polling the Oracle control queue");
                }

                try
                {
                    await Task.Delay(1.Seconds(), _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, _cancellation.Token);

        Address = endpoint.Uri;

        _retryBlock = new RetryBlock<Envelope>(deleteEnvelopeAsync, logger, cancellationToken);
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public Uri Address { get; }

    public async ValueTask CompleteAsync(Envelope envelope)
    {
        await _retryBlock.PostAsync(envelope);
    }

    public ValueTask DeferAsync(Envelope envelope) => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _receivingLoop.SafeDispose();
    }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();
        if (_receivingLoop != null)
        {
            try
            {
#pragma warning disable VSTHRD003 // Avoid awaiting foreign Tasks
                await _receivingLoop;
#pragma warning restore VSTHRD003 // Avoid awaiting foreign Tasks
            }
            catch (OperationCanceledException)
            {
            }

            _receivingLoop.Dispose();
        }
    }

    private async Task pollOnceAsync(CancellationToken token)
    {
        if (_transport.Database.HasDisposed) return;

        await using var conn = await _transport.Database.OracleDataSource.OpenConnectionAsync(token);
        try
        {
            // 1) Drop expired control messages so the queue table doesn't grow unbounded.
            await using (var deleteExpiredCmd = conn.CreateCommand(
                             $"DELETE FROM {_transport.TableName.QualifiedName} WHERE expires < :utcnow"))
            {
                deleteExpiredCmd.With("utcnow", DateTimeOffset.UtcNow);
                await deleteExpiredCmd.ExecuteNonQueryAsync(token);
            }

            // 2) Pull anything addressed to this node.
            var envelopes = new List<Envelope>();
            await using (var selectCmd = conn.CreateCommand(
                             $"SELECT body FROM {_transport.TableName.QualifiedName} WHERE node_id = :node"))
            {
                selectCmd.With("node", _transport.Options.UniqueNodeId);

                await using var reader = await selectCmd.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    var body = await reader.GetFieldValueAsync<byte[]>(0, token);
                    envelopes.Add(EnvelopeSerializer.Deserialize(body));
                }
            }

            if (envelopes.Count == 0) return;

            await _receiver.ReceivedAsync(this, envelopes.ToArray());

            // 3) Remove what we delivered. Failures land on the retry block via CompleteAsync,
            //    matching DatabaseControlTransport's batched-delete semantics, but doing it here
            //    inline keeps the implementation simple - the volume is small.
            await _transport.DeleteEnvelopesAsync(envelopes, token);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task deleteEnvelopeAsync(Envelope envelope, CancellationToken cancellationToken)
    {
        if (_transport.Database.HasDisposed)
        {
            return;
        }

        await using var conn =
            await _transport.Database.OracleDataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            await using var cmd = conn.CreateCommand(
                $"DELETE FROM {_transport.TableName.QualifiedName} WHERE id = :id");
            cmd.With("id", envelope.Id);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
