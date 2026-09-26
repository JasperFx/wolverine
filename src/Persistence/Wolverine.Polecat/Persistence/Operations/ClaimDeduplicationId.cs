using Microsoft.Data.SqlClient;
using Polecat;
using Wolverine.RDBMS;

namespace Wolverine.Polecat.Persistence.Operations;

/// <summary>
/// GH-4570. Writes a logical deduplication claim as part of the Polecat session's own transaction, so the
/// claim commits with the handler's events and documents — and rolls back with them.
///
/// <para>
/// An <see cref="ITransactionParticipant" /> rather than the queued <c>IStorageOperation</c> the Marten
/// twin uses, and the difference is not stylistic. Wolverine's message store on Polecat is built from
/// <c>SqlClientFactory.CreateDataSource(connectionString)</c> — the same SQL Server database as the
/// document store, but a <b>different connection pool</b>. A claim written on a connection of its own is
/// committed independently of whatever the handler is doing and survives its rollback, which is exactly
/// the defect GH-4505 exists to close. A participant is handed the live connection and transaction, so it
/// writes where the handler commits.
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

    public async Task BeforeCommitAsync(SqlConnection connection, SqlTransaction transaction, CancellationToken token)
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
