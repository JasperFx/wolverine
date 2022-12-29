using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Threading.Tasks;
using Weasel.Core;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS;

public abstract partial class MessageDatabase<T>
{
    private readonly string _deleteIncomingEnvelopeById;
    private readonly string _incrementIncominEnvelopeAttempts;
    public abstract Task MoveToDeadLetterStorageAsync(ErrorReport[] errors);
    public abstract Task DeleteIncomingEnvelopesAsync(Envelope[] envelopes);

    public abstract Task<IReadOnlyList<Envelope>> LoadPageOfGloballyOwnedIncomingAsync(Uri listenerAddress, int limit);
    public abstract Task ReassignIncomingAsync(int ownerId, IReadOnlyList<Envelope> incoming);

    public Task MarkIncomingEnvelopeAsHandledAsync(Envelope envelope)
    {
        return DatabaseSettings
            .CreateCommand(_deleteIncomingEnvelopeById)
            .With("id", envelope.Id)
            .With("keepUntil", DateTimeOffset.UtcNow.Add(Settings.KeepAfterMessageHandling))
            .ExecuteOnce(_cancellation);
    }

    public Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, DatabaseSettings);

        cmd.Transaction = tx;
        cmd.Connection = tx.Connection;

        return cmd.ExecuteNonQueryAsync(_cancellation);
    }


    public Task MoveToDeadLetterStorageAsync(Envelope envelope, Exception ex)
    {
        return MoveToDeadLetterStorageAsync(new[] { new ErrorReport(envelope, ex) });
    }

    public Task IncrementIncomingEnvelopeAttemptsAsync(Envelope envelope)
    {
        return DatabaseSettings.CreateCommand(_incrementIncominEnvelopeAttempts)
            .With("attempts", envelope.Attempts)
            .With("id", envelope.Id)
            .ExecuteOnce(_cancellation);
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        var builder = DatabaseSettings.ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(DatabaseSettings, builder, envelope);

        var cmd = builder.Compile();
        try
        {
            await cmd.ExecuteOnce(_cancellation).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            if (isExceptionFromDuplicateEnvelope(e))
            {
                throw new DuplicateIncomingEnvelopeException(envelope.Id);
            }

            throw;
        }
    }

    public async Task StoreIncomingAsync(IReadOnlyList<Envelope> envelopes)
    {
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, DatabaseSettings);

        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        cmd.Connection = conn;

        await cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public async Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id)
    {
        await using var conn = DatabaseSettings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        var cmd = conn.CreateCommand(
            $"select {DatabaseConstants.DeadLetterFields} from {DatabaseSettings.SchemaName}.{DatabaseConstants.DeadLetterTable} where id = @id");
        cmd.With("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(_cancellation);
        if (!await reader.ReadAsync(_cancellation))
        {
            return null;
        }

        var envelope = new Envelope
        {
            Id = await reader.GetFieldValueAsync<Guid>(0, _cancellation)
        };

        if (!await reader.IsDBNullAsync(1, _cancellation))
        {
            envelope.ScheduledTime = await reader.GetFieldValueAsync<DateTimeOffset>(1, _cancellation);
        }

        envelope.Attempts = await reader.GetFieldValueAsync<int>(2, _cancellation);
        envelope.Data = await reader.GetFieldValueAsync<byte[]>(3, _cancellation);
        envelope.ConversationId = await reader.MaybeReadAsync<Guid>(4, _cancellation);
        envelope.CorrelationId = await reader.MaybeReadAsync<string>(5, _cancellation);
        envelope.ParentId = await reader.MaybeReadAsync<string>(6, _cancellation);
        envelope.SagaId = await reader.MaybeReadAsync<string>(7, _cancellation);
        envelope.MessageType = await reader.GetFieldValueAsync<string>(8, _cancellation);
        envelope.ContentType = await reader.GetFieldValueAsync<string>(9, _cancellation);
        envelope.ReplyRequested = await reader.MaybeReadAsync<string>(10, _cancellation);
        envelope.AckRequested = await reader.GetFieldValueAsync<bool>(11, _cancellation);
        envelope.ReplyUri = await reader.ReadUriAsync(12, _cancellation);
        envelope.Source = await reader.MaybeReadAsync<string>(13, _cancellation);

        var report = new ErrorReport(envelope)
        {
            Explanation = await reader.GetFieldValueAsync<string>(14, _cancellation),
            ExceptionText = await reader.GetFieldValueAsync<string>(15, _cancellation),
            ExceptionType = await reader.GetFieldValueAsync<string>(16, _cancellation),
            ExceptionMessage = await reader.GetFieldValueAsync<string>(17, _cancellation)
        };

        return report;
    }

    public Task DeleteExpiredHandledEnvelopesAsync(DateTimeOffset utcNow)
    {
        if (Session.Transaction == null)
        {
            throw new InvalidOperationException("No current transaction");
        }

        return Session.CreateCommand(_deleteExpiredHandledEnvelopes)
            .With("time", utcNow)
            .ExecuteNonQueryAsync(_cancellation);
    }

    protected abstract bool isExceptionFromDuplicateEnvelope(Exception ex);
}