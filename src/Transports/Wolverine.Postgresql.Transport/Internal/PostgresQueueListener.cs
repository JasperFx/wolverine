using System.Collections.Concurrent;
using Npgsql;

namespace Wolverine.Transports.Postgresql.Internal;

internal sealed class PostgresQueueListener
{
    private readonly ConcurrentDictionary<Guid, MessageTransaction> _transactions = new();

    private readonly PostgresQueue _queue;
    private readonly CountWaitHandle _countWaitHandle;

    public PostgresQueueListener(PostgresQueue queue)
    {
        _queue = queue;
        _countWaitHandle = new CountWaitHandle(queue.MaximumConcurrentMessages);
    }

    public async ValueTask<PostgresMessage?> ReadNext(CancellationToken cancellationToken)
    {
        await using var waitHandle = await _queue.Transport.Client
            .GetChannelWaitHandleAsync(_queue.ChannelName, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            await _countWaitHandle.WaitOneAsync(cancellationToken);
            if (await TryDequeueNextMessageAsync(cancellationToken) is { } transaction)
            {
                // we are already processing this message for some reason. This should never happen.
                // we will just abort the transaction and try again.
                if (!_transactions.TryAdd(transaction.Message.Id, transaction))
                {
                    await transaction.AbortAsync(cancellationToken);
                }

                return transaction.Message;
            }

            await waitHandle.WaitOneAsync(cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Tries to dequeue the next message from the queue over the postgress client.
    /// It uses UPDATE SKIPED LOCKED to ensure that only one transaction is active at a time on a
    /// message.
    /// </summary>
    /// <param name="cancellationToken">
    ///   The cancellation token to use to cancel the operation.
    /// </param>
    /// <returns>
    ///  The message transaction if a message was found, otherwise null.
    /// </returns>
    private async ValueTask<MessageTransaction?> TryDequeueNextMessageAsync(
        CancellationToken cancellationToken)
    {
        // we do not dispose the connection here, because we keep the transaction open, until the
        // message is completed or aborted.
        var connection =
            await _queue.Transport.Client.DataSource.OpenConnectionAsync(cancellationToken);

        var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var command = connection.CreateCommand();

            command.Transaction = transaction;
            command.CommandText = $"""
                UPDATE {_queue.QueueName} 
                SET Attempts = Attempts + 1
                WHERE Id = (
                    SELECT Id 
                    FROM {_queue.QueueName} 
                    WHERE ScheduledTime <= NOW()
                    ORDER BY ScheduledTime
                    FOR UPDATE SKIP LOCKED
                    LIMIT 1 
                )
                RETURNING Id, CorrelationId, MessageType, ContentType, ScheduledTime, Data, Headers, Attempts, SenderId;
            """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);

            // read the first row into message transaction. if there is no row, return null.
            if (await reader.ReadAsync(cancellationToken))
            {
                var message = new PostgresMessage
                {
                    Id = reader.GetFieldValue<Guid>(0),
                    CorrelationId = reader.GetFieldValue<string>(1),
                    MessageType = reader.GetFieldValue<string>(2),
                    ContentType = reader.GetFieldValue<string>(3),
                    ScheduledTime = reader.GetFieldValue<DateTimeOffset>(4),
                    Data = reader.GetFieldValue<byte[]>(5),
                    Headers = reader.GetFieldValue<Dictionary<string, string>>(6),
                    Attempts = reader.GetFieldValue<int>(7),
                    SenderId = reader.GetFieldValue<Guid>(8)
                };

                return new MessageTransaction(message.Id, transaction, message);
            }

            await transaction.DisposeAsync();
            await connection.DisposeAsync();

            return null;
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            await transaction.DisposeAsync();
            await connection.DisposeAsync();

            throw;
        }
    }

    public async Task CompleteAsync(Guid messageId, CancellationToken cancellationToken)
    {
        if (_transactions.TryRemove(messageId, out var transaction))
        {
            try
            {
                _countWaitHandle.Release();

                await using var command = transaction.Transaction.Connection!.CreateCommand();
                command.Transaction = transaction.Transaction;
                command.CommandText = $"""
                    DELETE FROM {_queue.QueueName} 
                    WHERE Id = @Id;
                """;

                command.Parameters.AddWithValue("Id", messageId);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            finally
            {
                await transaction.Transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Could not find transaction for message {messageId}");
        }
    }

    public async Task DeferAsync(Guid messageId, CancellationToken cancellationToken)
    {
        if (_transactions.TryRemove(messageId, out var transaction))
        {
            try
            {
                _countWaitHandle.Release();
            }
            finally
            {
                await transaction.Transaction.CommitAsync(cancellationToken);
                await transaction.DisposeAsync();
            }
        }
        else
        {
            throw new InvalidOperationException(
                $"Could not find transaction for message {messageId}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var transaction in _transactions.Values)
        {
            await transaction.DisposeAsync();
        }

        _countWaitHandle.Dispose();
    }

    private struct MessageTransaction : IAsyncDisposable
    {
        public MessageTransaction(
            Guid id,
            NpgsqlTransaction transaction,
            PostgresMessage message)
        {
            Id = id;
            Transaction = transaction;
            Message = message;
        }

        public Guid Id { get; set; }

        public NpgsqlTransaction Transaction { get; set; }

        public PostgresMessage Message { get; set; }

        public async Task AbortAsync(CancellationToken cancellationToken)
        {
            await Transaction.RollbackAsync(cancellationToken);
            await DisposeAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await Transaction.Connection!.DisposeAsync();
            await Transaction.DisposeAsync();
        }
    }

    private class CountWaitHandle : IDisposable
    {
        private readonly SemaphoreSlim _semaphoreSlim;

        public CountWaitHandle(int maxCount)
        {
            _semaphoreSlim = new SemaphoreSlim(maxCount, maxCount);
        }

        public async Task WaitOneAsync(CancellationToken cancellationToken)
            => await _semaphoreSlim.WaitAsync(cancellationToken);

        public void Release()
            => _semaphoreSlim.Release();

        public void Dispose()
            => _semaphoreSlim.Dispose();
    }
}
