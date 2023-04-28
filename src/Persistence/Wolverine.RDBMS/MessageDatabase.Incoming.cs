using System.Data.Common;
using Weasel.Core;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime.Serialization;

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
        return Settings
            .CreateCommand(_deleteIncomingEnvelopeById)
            .With("id", envelope.Id)
            .With("keepUntil", DateTimeOffset.UtcNow.Add(Durability.KeepAfterMessageHandling))
            .ExecuteOnce(_cancellation);
    }

    public Task StoreIncomingAsync(DbTransaction tx, Envelope[] envelopes)
    {
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, Settings);

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
        return Settings.CreateCommand(_incrementIncominEnvelopeAttempts)
            .With("attempts", envelope.Attempts)
            .With("id", envelope.Id)
            .ExecuteOnce(_cancellation);
    }

    public async Task StoreIncomingAsync(Envelope envelope)
    {
        var builder = Settings.ToCommandBuilder();
        DatabasePersistence.BuildIncomingStorageCommand(Settings, builder, envelope);

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
        var cmd = DatabasePersistence.BuildIncomingStorageCommand(envelopes, Settings);

        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        cmd.Connection = conn;

        await cmd.ExecuteNonQueryAsync(_cancellation);
    }

    public async Task<ErrorReport?> LoadDeadLetterEnvelopeAsync(Guid id)
    {
        await using var conn = Settings.CreateConnection();
        await conn.OpenAsync(_cancellation);

        var cmd = conn.CreateCommand(
            $"select {DatabaseConstants.DeadLetterFields} from {Settings.SchemaName}.{DatabaseConstants.DeadLetterTable} where id = @id");
        cmd.With("id", id);

        await using var reader = await cmd.ExecuteReaderAsync(_cancellation);
        if (!await reader.ReadAsync(_cancellation))
        {
            return null;
        }

        var body = await reader.GetFieldValueAsync<byte[]>(2, _cancellation);
        var envelope = EnvelopeSerializer.Deserialize(body);

        var report = new ErrorReport(envelope)
        {
            ExceptionType = await reader.GetFieldValueAsync<string>(6, _cancellation),
            ExceptionMessage = await reader.GetFieldValueAsync<string>(7, _cancellation)
        };

        return report;
    }

    protected abstract bool isExceptionFromDuplicateEnvelope(Exception ex);
}