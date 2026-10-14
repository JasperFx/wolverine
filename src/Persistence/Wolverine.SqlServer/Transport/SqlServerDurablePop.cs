using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Wolverine.RDBMS;
using Wolverine.Runtime.Serialization;

namespace Wolverine.SqlServer.Transport;

internal record UnconfirmedPop(Guid[] Ids, int NodeNumber);

/// <summary>
///     GH-5015. The durable pop of a <see cref="SqlServerQueue" />: move a batch from the queue table into the
///     inbox (status Incoming, owner_id = this node) and hand it back. Shared by
///     <see cref="SqlServerQueueListener" /> and the queue's own test helper.
/// </summary>
/// <remarks>
///     The move runs in an explicit transaction that commits only after every moved row has been read.
///     Run as separate auto-committed statements, the move was durable before the client read the reply, so a
///     read failure (a timeout during a disk stall) left the rows owned by this live node and in no listener's
///     memory -- and recovery only reclaims owner_id = 0. A failed read now rolls the move back. A COMMIT that
///     fails on the wire may still have committed, so its envelopes are released to recovery, and kept to retry
///     the release when even that fails.
/// </remarks>
internal class SqlServerDurablePop
{
    private readonly string _connectionString;
    private readonly string _popSql;
    private readonly string _incomingTable;
    private readonly string _queueName;
    private readonly Uri _address;
    private UnconfirmedPop? _unconfirmedPop;

    public SqlServerDurablePop(SqlServerQueue queue, string connectionString, Uri address)
    {
        _connectionString = connectionString;
        _address = address;
        _queueName = queue.Name;
        _popSql = SqlServerQueueSql.TryPopToInbox(queue, address);
        _incomingTable = $"{queue.Parent.MessageStorageSchemaName}.{DatabaseConstants.IncomingTable}";
    }

    public bool HasUnconfirmedPop => _unconfirmedPop != null;

    public async Task<IReadOnlyList<Envelope>> TryPopAsync(int count, int nodeNumber, ILogger logger,
        CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            await using var tx = (SqlTransaction)await conn.BeginTransactionAsync(cancellationToken);

            var ids = new List<Guid>();
            var list = new List<Envelope>();

            await using (var cmd = conn.CreateCommand())
            {
                cmd.Transaction = tx;
                cmd.CommandText = _popSql;
                cmd.Parameters.AddWithValue("count", count);
                cmd.Parameters.AddWithValue("node", nodeNumber);

                await using var reader = await cmd.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    ids.Add(reader.GetGuid(0));
                    var data = await reader.GetFieldValueAsync<byte[]>(1, cancellationToken);
                    try
                    {
                        list.Add(EnvelopeSerializer.Deserialize(data));
                    }
                    catch (Exception e)
                    {
                        logger.LogError(e,
                            "Error trying to deserialize Envelope data in Sql Transport Queue {Queue}, discarding",
                            _queueName);
                        list.Add(Envelope.ForPing(_address)); // just a stand in
                    }
                }
            }

            try
            {
                await tx.CommitAsync(cancellationToken);
            }
            catch (Exception e)
            {
                // Nothing is handed to the receiver: if the move rolled back, the rows are back in the queue and
                // would run twice
                await releaseAfterFailedCommitAsync(ids, nodeNumber, e, logger);
                throw;
            }

            return list;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task releaseAfterFailedCommitAsync(List<Guid> ids, int nodeNumber, Exception commitFailure,
        ILogger logger)
    {
        if (ids.Count == 0) return;

        logger.LogError(commitFailure,
            "The commit of a durable pop of {Count} envelope(s) from SQL Server queue {Queue} failed with an unknown outcome. Releasing them to recovery",
            ids.Count, _queueName);

        _unconfirmedPop = new UnconfirmedPop(ids.ToArray(), nodeNumber);
        try
        {
            await ReleaseUnconfirmedPopAsync(CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e,
                "Unable to release {Count} envelope(s) of an unconfirmed durable pop from SQL Server queue {Queue}. Retrying on the next poll",
                ids.Count, _queueName);
        }
    }

    /// <summary>
    ///     Hand the envelopes of a pop whose COMMIT outcome is unknown to recovery (owner_id = 0). READCOMMITTEDLOCK
    ///     makes the update wait on the inbox rows the original transaction inserted, even under read committed
    ///     snapshot, so it cannot slip past a move that is still in flight: once it runs, the move has either
    ///     committed (the rows are released) or rolled back (the rows are back in the queue, nothing matches).
    /// </summary>
    public async Task ReleaseUnconfirmedPopAsync(CancellationToken cancellationToken)
    {
        if (_unconfirmedPop is not { } pop) return;

        await using var conn = new SqlConnection(_connectionString);
        await conn.OpenAsync(cancellationToken);

        try
        {
            await using var cmd = conn.CreateCommand();

            var idParameters = new string[pop.Ids.Length];
            for (var i = 0; i < pop.Ids.Length; i++)
            {
                idParameters[i] = "@id" + i;
                cmd.Parameters.AddWithValue(idParameters[i], pop.Ids[i]);
            }

            cmd.Parameters.AddWithValue("@node", pop.NodeNumber);
            cmd.CommandText =
                $"update {_incomingTable} with (READCOMMITTEDLOCK) set {DatabaseConstants.OwnerId} = 0 where id in ({string.Join(", ", idParameters)}) and {DatabaseConstants.OwnerId} = @node and {DatabaseConstants.Status} = '{EnvelopeStatus.Incoming}'";

            await cmd.ExecuteNonQueryAsync(cancellationToken);
            _unconfirmedPop = null;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }
}
