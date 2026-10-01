using System.Data.Common;
using JasperFx.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Weasel.Core;
using Wolverine.Configuration;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;

namespace Wolverine.Sqlite.Transport;

internal class SqliteQueueListener : IListener
{
    private readonly CancellationTokenSource _cancellation = new();
    private readonly SqliteQueue _queue;
    private readonly IReceiver _receiver;
    private readonly DbDataSource _dataSource;
    private readonly string? _databaseName;
    private readonly ILogger<SqliteQueueListener> _logger;
    private Task? _task;
    private readonly DurabilitySettings _settings;
    private Task? _scheduledTask;
    private readonly SqliteQueueSender _sender;
    private readonly string _queueTableName;
    private readonly string _queueName;
    private readonly string _scheduledTableName;
    private readonly TimeSpan _pollingInterval;
    private readonly SqliteMessageStore? _store;
    private readonly string _incomingTableName;
    private readonly string _selectReadySql;

    public SqliteQueueListener(SqliteQueue queue, IWolverineRuntime runtime, IReceiver receiver,
        DbDataSource dataSource, string? databaseName)
    {
        Address = SqliteQueue.ToUri(queue.Name, databaseName);
        _queue = queue;
        _receiver = receiver;
        _dataSource = dataSource;
        _databaseName = databaseName;
        _logger = runtime.LoggerFactory.CreateLogger<SqliteQueueListener>();
        _settings = runtime.DurabilitySettings;
        _pollingInterval = queue.PollingInterval ?? _settings.ScheduledJobPollingTime;

        _sender = new SqliteQueueSender(queue, _dataSource, databaseName);

        _queueTableName = _queue.QueueTable.Identifier.QualifiedName;
        _scheduledTableName = _queue.ScheduledTable.Identifier.QualifiedName;

        _queueName = _queue.Name;

        // GH-4758: the durable dequeue writes the inbox row itself, so it needs the incoming table under
        // whatever prefix the message store's schema name applies on SQLite (GH-3943). Exactly the lookup
        // SqliteQueueSender already makes for the outgoing table.
        _store = queue.Parent.Store;
        _incomingTableName = _store?.TableNameFor(DatabaseConstants.IncomingTable)
                             ?? DatabaseConstants.IncomingTable;

        _selectReadySql = $@"
                        SELECT {DatabaseConstants.Id}, {DatabaseConstants.Body}
                        FROM {_queueTableName}
                        ORDER BY timestamp
                        LIMIT {_queue.MaximumMessagesToReceive}
                    ";
    }

    public IHandlerPipeline? Pipeline => _receiver.Pipeline;

    public ValueTask CompleteAsync(Envelope envelope)
    {
        return ValueTask.CompletedTask;
    }

    public async ValueTask DeferAsync(Envelope envelope)
    {
        await _sender.SendAsync(envelope, _cancellation.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
        _task.SafeDispose();
        _scheduledTask.SafeDispose();
    }

    public Uri Address { get; }

    public async ValueTask StopAsync()
    {
        await _cancellation.CancelAsync();

        _task?.SafeDispose();
        _scheduledTask?.SafeDispose();
    }

    private async Task lookForScheduledMessagesAsync()
    {
        await Task.Delay(_settings.ScheduledJobFirstExecution);

        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                var count = await MoveScheduledToReadyQueueAsync(_cancellation.Token);
                if (count > 0)
                {
                    _logger.LogInformation(
                        "Propagated {Number} scheduled messages to SQLite-backed queue {Queue}", count, _queueName);
                }

                await DeleteExpiredAsync(CancellationToken.None);

                failedCount = 0;

                await Task.Delay(_pollingInterval);
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 100).Milliseconds();

                _logger.LogError(e, "Error while trying to propagate scheduled messages from SQLite Queue {Name}",
                    _queueName);

                await Task.Delay(pauseTime);
            }
        }
    }

    public async Task<long> MoveScheduledToReadyQueueAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        long count = 0;

        try
        {
            // SQLite doesn't support complex CTEs like PostgreSQL, so we use a simpler approach
            var tx = await conn.BeginTransactionAsync(cancellationToken);

            // Move scheduled messages that are ready
            await using var moveCommand = conn.CreateCommand();
            moveCommand.Transaction = tx;
            moveCommand.CommandText = $@"
                INSERT INTO {_queueTableName} (id, body, message_type)
                SELECT id, body, message_type
                FROM {_scheduledTableName}
                WHERE julianday(execution_time) <= julianday('now')
                AND id NOT IN (SELECT id FROM {_queueTableName})
            ";

            await moveCommand.ExecuteNonQueryAsync(cancellationToken);

            // Delete moved messages from scheduled table
            await using var deleteCommand = conn.CreateCommand();
            deleteCommand.Transaction = tx;
            deleteCommand.CommandText = $@"
                DELETE FROM {_scheduledTableName}
                WHERE julianday(execution_time) <= julianday('now')
            ";

            count = await deleteCommand.ExecuteNonQueryAsync(cancellationToken);

            await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            await conn.CloseAsync();
        }

        return count;
    }

    public async Task DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);

        await conn.CreateCommand($"delete from {_queueTableName} where keep_until is not null and julianday(keep_until) < julianday('now')")
            .ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StartAsync()
    {
        _task = Task.Run(tryPopMessages, _cancellation.Token);
        _scheduledTask = Task.Run(lookForScheduledMessagesAsync, _cancellation.Token);

        await Task.CompletedTask;
    }

    private async Task tryPopMessages()
    {
        await Task.Delay(_settings.FirstNodeReassignmentExecution);

        var failedCount = 0;

        while (!_cancellation.Token.IsCancellationRequested)
        {
            try
            {
                // GH-4758: a durable listener must not expose an envelope to the receiver until the inbox
                // row for it is committed. The buffered path keeps the destructive dequeue it has always
                // had -- it asked for no durability and must not pay for any.
                var envelopes = _queue.Mode == EndpointMode.Durable
                    ? await TryPopDurablyAsync(_cancellation.Token)
                    : await TryPopAsync(_cancellation.Token);

                if (envelopes.Count > 0)
                {
                    foreach (var envelope in envelopes)
                    {
                        await _receiver.ReceivedAsync(this, envelope);
                    }
                }
                else
                {
                    // Opportunistically promote due scheduled messages when the ready queue is empty.
                    // This keeps delayed delivery responsive even if the background scheduler is delayed.
                    await MoveScheduledToReadyQueueAsync(_cancellation.Token);

                    await Task.Delay(_pollingInterval, _cancellation.Token);
                }

                failedCount = 0;
            }
            catch (Exception e)
            {
                if (e is TaskCanceledException && _cancellation.IsCancellationRequested)
                {
                    break;
                }

                failedCount++;
                var pauseTime = failedCount > 5 ? 1.Seconds() : (failedCount * 250).Milliseconds();

                _logger.LogError(e, "Error trying to pop messages from SQLite queue {Name}", _queueName);

                await Task.Delay(pauseTime);
            }
        }
    }

    /// <summary>
    /// The buffered dequeue: select a batch and delete it in one transaction. Nothing is written to the
    /// inbox, because a <see cref="EndpointMode.BufferedInMemory" /> listener did not ask for durability.
    /// </summary>
    public async Task<IReadOnlyList<Envelope>> TryPopAsync(CancellationToken token)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);

        try
        {
            await using var tx = await conn.BeginTransactionAsync(token);

            var rows = await readReadyRowsAsync(conn, tx, token);
            if (rows.Count == 0)
            {
                await tx.RollbackAsync(token);
                return [];
            }

            await deleteRowsAsync(conn, tx, rows, token);
            await tx.CommitAsync(token);

            return deserialize(rows, null);
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    /// <summary>
    /// GH-4758. The durable dequeue. Moving the selected rows out of the queue table and into the message
    /// store's incoming-envelope table happens in ONE transaction, so there is no committed state in which
    /// the queue row is gone, no inbox row exists, and a handler is running from memory alone. That state
    /// was reachable before: <see cref="SqliteQueue" /> implements <c>IDatabaseBackedEndpoint</c>, which
    /// tells <c>DurableReceiver</c> the transport has already persisted the envelope and to skip its own
    /// inbox INSERT -- a promise this listener did not keep.
    /// </summary>
    /// <remarks>
    /// <para>On write concurrency: Microsoft.Data.Sqlite's <c>BeginTransaction</c> defaults to
    /// <c>deferred: false</c>, i.e. <c>BEGIN IMMEDIATE</c>, so the database's single write lock is taken
    /// HERE rather than at the first write statement. That is what makes it safe to put the SELECT, the
    /// DELETE and the INSERT in one transaction: a DEFERRED transaction that reads and then writes has to
    /// upgrade its lock, and SQLite answers a contended upgrade with SQLITE_BUSY that no <c>busy_timeout</c>
    /// can wait out. At BEGIN the busy handler does apply, so a second listener (or any other writer in the
    /// process) simply waits its turn -- <c>SqliteConnection.DefaultTimeout</c>, 30 seconds by default --
    /// and a genuine timeout surfaces as the backoff-and-retry in <c>tryPopMessages</c> with every queue row
    /// still in place. SQLite has no <c>FOR UPDATE SKIP LOCKED</c>, and with one writer at a time it needs
    /// none: concurrent listeners serialize rather than interleave.</para>
    ///
    /// <para>On idempotency: the inbox row is written with <c>status = 'Incoming'</c> and
    /// <c>owner_id = </c> this node's assigned number, exactly as the PostgreSQL and SQL Server durable pops
    /// do. The recovery sweep only reclaims rows at <c>owner_id = 0</c>
    /// (<c>LoadPageOfGloballyOwnedIncomingAsync</c>), so the local listener and the sweep cannot both claim
    /// the same envelope. Ownership is released back to 0 on a clean drain, or by the dead-node sweep.</para>
    /// </remarks>
    public async Task<IReadOnlyList<Envelope>> TryPopDurablyAsync(CancellationToken token)
    {
        if (_store == null)
        {
            throw new InvalidOperationException(
                $"The SQLite queue at {Address} is listening in durable mode, but no SQLite message store is registered to hold its incoming envelopes");
        }

        await using var conn = await _dataSource.OpenConnectionAsync(token).ConfigureAwait(false);

        try
        {
            await using var tx = await conn.BeginTransactionAsync(token);

            var rows = await readReadyRowsAsync(conn, tx, token);
            if (rows.Count == 0)
            {
                await tx.RollbackAsync(token);
                return [];
            }

            // Delete by the raw id text read back from the row rather than by the deserialized envelope's
            // Guid. A body we cannot deserialize then still leaves the queue instead of being re-selected
            // forever, and no Guid rendering has to agree with the text the sender wrote.
            await deleteRowsAsync(conn, tx, rows, token);

            // The guard PostgreSQL and SQL Server apply as a pre-delete (GH-4316): a queue row whose
            // envelope is ALREADY in this address's inbox must not be inserted a second time. It gets back
            // onto the queue when a latched receiver defers an already-persisted envelope. Those rows are
            // dropped -- deleted from the queue above and not re-offered -- exactly as theirs are.
            //
            // Probed against the batch's own ids rather than, as theirs do, against every inbox row for this
            // address. The result is the same and the cost is not: on SQLite this runs inside a lock over the
            // whole database file, with one writer for the entire application, so a scan of the inbox here
            // stalls every other write in the process. Keyed on the id, so it is a primary-key lookup
            // bounded by MaximumMessagesToReceive. Same reasoning as the bounded reaps in
            // SqliteMessageStore (GH-4567).
            var alreadyInboxed = await findAlreadyInboxedAsync(conn, tx, rows, token);

            var envelopes = deserialize(rows, alreadyInboxed);
            foreach (var envelope in envelopes)
            {
                envelope.Status = EnvelopeStatus.Incoming;
                envelope.OwnerId = _settings.AssignedNodeNumber;

                // GH-4288: tells DurableReceiver this envelope is already in the inbox, so no path through
                // it -- including the latched one -- writes a second row.
                envelope.WasPersistedInInbox = true;
            }

            if (envelopes.Count > 0)
            {
                // The store's own inbox INSERT, on this transaction. Reused rather than hand-written so the
                // columns and the parameter binding cannot drift from every other inbox write -- which
                // matters more here than it looks: the inbox matches ids with `where id = @id` bound as a
                // Guid, and Microsoft.Data.Sqlite renders that UPPER case, while the queue tables hold
                // lower-case text. An `insert ... select id from queue` would have written rows that
                // mark-as-handled could never retire.
                await _store.StoreIncomingAsync(tx, envelopes.ToArray());
            }

            await tx.CommitAsync(token);

            return envelopes;
        }
        finally
        {
            await conn.CloseAsync();
        }
    }

    private async Task<List<(string RawId, byte[] Body)>> readReadyRowsAsync(DbConnection conn, DbTransaction tx,
        CancellationToken token)
    {
        await using var cmd = conn.CreateCommand(_selectReadySql);
        cmd.Transaction = tx;

        var rows = new List<(string, byte[])>();

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            rows.Add((reader.GetString(0), (byte[])reader.GetValue(1)));
        }

        await reader.CloseAsync();

        return rows;
    }

    private async Task deleteRowsAsync(DbConnection conn, DbTransaction tx,
        IReadOnlyList<(string RawId, byte[] Body)> rows, CancellationToken token)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        var placeholders = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            placeholders[i] = $"@id{i}";
            cmd.With($"id{i}", rows[i].RawId);
        }

        cmd.CommandText =
            $"DELETE FROM {_queueTableName} WHERE {DatabaseConstants.Id} IN ({string.Join(", ", placeholders)})";

        await cmd.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// The ids in this batch that already have an inbox row at this listener's address, as lower-case text.
    /// The <c>lower()</c> is not decoration: the transport tables hold ids as lower-case "D" text (the
    /// sender binds a string), while the inbox holds whatever Microsoft.Data.Sqlite renders a Guid parameter
    /// as, which is UPPER case.
    /// </summary>
    private async Task<HashSet<string>> findAlreadyInboxedAsync(DbConnection conn, DbTransaction tx,
        IReadOnlyList<(string RawId, byte[] Body)> rows, CancellationToken token)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;

        var placeholders = new string[rows.Count];
        for (var i = 0; i < rows.Count; i++)
        {
            placeholders[i] = $"@id{i}";
            cmd.With($"id{i}", rows[i].RawId.ToLowerInvariant());
        }

        cmd.CommandText =
            $"select lower({DatabaseConstants.Id}) from {_incomingTableName} where lower({DatabaseConstants.Id}) in ({string.Join(", ", placeholders)}) and {DatabaseConstants.ReceivedAt} = @address";
        cmd.With("address", Address.ToString());

        var found = new HashSet<string>();

        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            found.Add(reader.GetString(0));
        }

        await reader.CloseAsync();

        return found;
    }

    private List<Envelope> deserialize(IReadOnlyList<(string RawId, byte[] Body)> rows,
        HashSet<string>? skip)
    {
        var envelopes = new List<Envelope>(rows.Count);

        foreach (var row in rows)
        {
            if (skip != null && skip.Contains(row.RawId.ToLowerInvariant()))
            {
                _logger.LogInformation(
                    "Discarding queue row {Id} from SQLite queue {Queue}: its envelope is already in the inbox for {Address}",
                    row.RawId, _queueName, Address);
                continue;
            }

            try
            {
                var envelope = EnvelopeSerializer.Deserialize(row.Body);

                // Destination is persisted as received_at and is half of the inbox identity under
                // MessageIdentity.IdAndDestination, so the durable pop has to stamp it BEFORE its insert.
                // MarkReceived sets the same value from the listener's address once the receiver takes the
                // envelope, which is why the buffered path never needed it explicitly.
                envelope.Destination = Address;

                envelopes.Add(envelope);
            }
            catch (Exception e)
            {
                // The row has already been deleted in this transaction. Nothing can execute an envelope we
                // cannot read, and writing it to the inbox would only strand a row no handler ever retires.
                _logger.LogError(e,
                    "Error trying to deserialize Envelope data for {Id} in SQLite Transport Queue {Queue}, discarding",
                    row.RawId, _queueName);
            }
        }

        return envelopes;
    }
}
