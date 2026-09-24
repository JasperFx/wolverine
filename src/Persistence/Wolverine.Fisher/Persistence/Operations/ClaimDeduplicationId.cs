using Fisher;
using Microsoft.Data.Sqlite;
using Wolverine.RDBMS;

namespace Wolverine.Fisher.Persistence.Operations;

/// <summary>
/// GH-4571. Writes a logical deduplication claim as part of the Fisher session's own transaction, so the
/// claim commits with the handler's events and documents — and rolls back with them.
///
/// <para>
/// An <see cref="ITransactionParticipant" /> rather than the queued <c>IStorageOperation</c> the Marten
/// twin uses, and on SQLite there is no alternative. Wolverine's message store here is a second
/// <c>SqliteConnection</c> to the same file, and a write on it while this session holds the file's write
/// lock blocks on a transaction that is waiting for it — a self-deadlock that presents as a hang. A
/// participant is handed the live connection and transaction, so it writes where the handler commits.
/// The pattern <see cref="StoreIncomingEnvelopeParticipant" /> already uses.
/// </para>
/// </summary>
internal class ClaimDeduplicationIdParticipant : ITransactionParticipant
{
    private readonly string _table;
    private readonly string _deduplicationId;
    private readonly DateTimeOffset _expires;

    public ClaimDeduplicationIdParticipant(string table, string deduplicationId, DateTimeOffset expires)
    {
        _table = table;
        _deduplicationId = deduplicationId;
        _expires = expires;
    }

    public async Task BeforeCommitAsync(SqliteConnection connection, SqliteTransaction transaction,
        CancellationToken token)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText =
            $"insert into {_table} ({DatabaseConstants.DeduplicationId}, {DatabaseConstants.Expires}) values (@id, @expires)";

        cmd.Parameters.AddWithValue("@id", _deduplicationId);
        cmd.Parameters.AddWithValue("@expires", _expires);

        await cmd.ExecuteNonQueryAsync(token);
    }
}
