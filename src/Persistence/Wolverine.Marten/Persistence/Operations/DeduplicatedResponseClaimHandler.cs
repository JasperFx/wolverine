using System.Data.Common;
using Marten.Linq.QueryHandlers;
using Weasel.Postgresql;
using Weasel.Storage;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

/// <summary>
/// GH-5160. The optimistic half of a <c>[DeduplicatedWithResponse]</c> claim written inside the Marten transaction:
/// "has an earlier request already committed this key, and with what?", asked through the session so it can ride
/// the batched query the chain was already running. The <c>wolverine_deduplicated_responses</c> twin of
/// <see cref="DeduplicationClaimExistsHandler" />, returning the claim rather than whether there is one, because a
/// repeat is answered from it.
///
/// <para>
/// Existence only, as there: the expiry column is not consulted, matching
/// <c>RdbmsReplayableDeduplicationStore.FindAsync</c>.
/// </para>
/// </summary>
internal class DeduplicatedResponseClaimHandler : IQueryHandler<DeduplicatedResponseClaim?>
{
    private readonly string _table;
    private readonly string? _deduplicationId;
    private readonly Action<string> _onFound;

    /// <param name="deduplicationId">Already hashed. Null for a request without a key, which finds nothing.</param>
    /// <param name="onFound">
    /// Invoked with the id when a claim is found, for the log line, from inside the batch for the reason
    /// <see cref="DeduplicationClaimExistsHandler" /> gives.
    /// </param>
    public DeduplicatedResponseClaimHandler(string table, string? deduplicationId, Action<string> onFound)
    {
        _table = table;
        _deduplicationId = deduplicationId;
        _onFound = onFound;
    }

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        builder.Append(
            $"select {DatabaseConstants.Fingerprint}, {DatabaseConstants.ResponseStatusCode}, {DatabaseConstants.ResponseBody}, {DatabaseConstants.ResponseLocation} from {_table} where {DatabaseConstants.DeduplicationId} = ");
        builder.AppendParameter((object?)_deduplicationId ?? DBNull.Value, NpgsqlTypes.NpgsqlDbType.Varchar);
    }

    [Obsolete("Synchronous querying is being removed from Marten")]
    public DeduplicatedResponseClaim? Handle(DbDataReader reader, IStorageSession session)
    {
        if (!reader.Read()) return null;

        return found(reader.GetString(0), reader.IsDBNull(1)
            ? null
            : new DeduplicatedResponse(reader.GetInt32(1), readString(reader, 2), readString(reader, 3)));
    }

    public async Task<DeduplicatedResponseClaim?> HandleAsync(DbDataReader reader, IStorageSession session,
        CancellationToken token)
    {
        if (!await reader.ReadAsync(token).ConfigureAwait(false)) return null;

        var fingerprint = await reader.GetFieldValueAsync<string>(0, token).ConfigureAwait(false);

        // No status: not answered yet
        if (await reader.IsDBNullAsync(1, token).ConfigureAwait(false)) return found(fingerprint, null);

        return found(fingerprint, new DeduplicatedResponse(
            await reader.GetFieldValueAsync<int>(1, token).ConfigureAwait(false),
            await readStringAsync(reader, 2, token).ConfigureAwait(false),
            await readStringAsync(reader, 3, token).ConfigureAwait(false)));
    }

    private DeduplicatedResponseClaim found(string fingerprint, DeduplicatedResponse? response)
    {
        _onFound(_deduplicationId!);
        return new DeduplicatedResponseClaim(fingerprint, response);
    }

    private static string? readString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static async Task<string?> readStringAsync(DbDataReader reader, int ordinal, CancellationToken token)
        => await reader.IsDBNullAsync(ordinal, token).ConfigureAwait(false)
            ? null
            : await reader.GetFieldValueAsync<string>(ordinal, token).ConfigureAwait(false);

    public Task<int> StreamJson(Stream stream, DbDataReader reader, CancellationToken token)
    {
        throw new NotSupportedException(
            $"{nameof(DeduplicatedResponseClaimHandler)} is an internal claim lookup and is never streamed as JSON");
    }
}
