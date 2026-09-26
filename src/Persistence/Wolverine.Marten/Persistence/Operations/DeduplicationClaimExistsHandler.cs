using System.Data.Common;
using Marten.Linq.QueryHandlers;
using Weasel.Postgresql;
using Weasel.Storage;
using Wolverine.RDBMS;

namespace Wolverine.Marten.Persistence.Operations;

/// <summary>
/// GH-4505. The optimistic half of a transactional deduplication claim: "has this logical id already
/// been claimed?", asked through the Marten session so it can ride the batched query the chain was
/// already running.
///
/// <para>
/// An <see cref="IQueryHandler{T}" /> rather than <c>IBatchedQuery.Query&lt;T&gt;(sql)</c> because that
/// overload is constrained to reference types and parses <c>?</c> placeholders out of the SQL. This
/// binds its one parameter directly and answers a <c>bool</c>.
/// </para>
///
/// <para>
/// <b>Optimistic, and deliberately so.</b> A queued INSERT cannot report "was that a duplicate?" before
/// the handler runs — that answer only exists at commit. So this refuses the overwhelmingly common
/// case, a caller retrying a key that is already recorded, before any work happens; two genuinely
/// concurrent callers can both read "not claimed", and the primary key on the deduplication table
/// arbitrates between them at <c>SaveChangesAsync</c>. See
/// <c>RefuseDuplicateDeduplicationClaimFrame</c> for what becomes of the loser.
/// </para>
///
/// <para>
/// Existence only — the expiry column is not consulted, matching
/// <c>RdbmsDeduplicationStore.TryClaimAsync</c>: the reaper runs on its own cadence, so a claim can
/// outlive its expiry, and treating that as still-claimed errs toward refusing work that was already
/// done. Erring the other way would run it twice.
/// </para>
/// </summary>
internal class DeduplicationClaimExistsHandler : IQueryHandler<bool>
{
    private readonly string _table;
    private readonly string _deduplicationId;
    private readonly Action<string> _onDuplicate;

    /// <param name="onDuplicate">
    /// Invoked with the id when the claim already exists. The log line lives with the caller that owns
    /// the deduplication window rather than here, but it has to fire from inside the batch: by the time
    /// the generated code awaits this item the refusal is already on its way out, and a batched check
    /// has no other moment at which "this was a duplicate" is known.
    /// </param>
    public DeduplicationClaimExistsHandler(string table, string deduplicationId, Action<string> onDuplicate)
    {
        _table = table;
        _deduplicationId = deduplicationId;
        _onDuplicate = onDuplicate;
    }

    public void ConfigureCommand(ICommandBuilder builder, IStorageSession session)
    {
        builder.Append(
            $"select 1 from {_table} where {DatabaseConstants.DeduplicationId} = ");
        builder.AppendParameter(_deduplicationId);
    }

    [Obsolete("Synchronous querying is being removed from Marten")]
    public bool Handle(DbDataReader reader, IStorageSession session)
    {
        return found(reader.Read());
    }

    public async Task<bool> HandleAsync(DbDataReader reader, IStorageSession session, CancellationToken token)
    {
        return found(await reader.ReadAsync(token).ConfigureAwait(false));
    }

    private bool found(bool exists)
    {
        if (exists) _onDuplicate(_deduplicationId);
        return exists;
    }

    public Task<int> StreamJson(Stream stream, DbDataReader reader, CancellationToken token)
    {
        throw new NotSupportedException(
            $"{nameof(DeduplicationClaimExistsHandler)} is an internal existence check and is never streamed as JSON");
    }
}
