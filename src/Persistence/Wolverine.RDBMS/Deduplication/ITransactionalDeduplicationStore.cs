using System.Data.Common;
using Wolverine.Persistence.Durability;

namespace Wolverine.RDBMS.Deduplication;

/// <summary>
/// GH-5108. An <see cref="IDeduplicationStore" /> that can write a logical deduplication claim on a
/// connection and transaction the CALLER owns, rather than on a connection of its own.
///
/// <para>
/// This is what lets a persistence provider that already holds a <see cref="DbTransaction" /> against the
/// message database -- EF Core's eager transactional middleware -- make the claim ride that transaction: it
/// commits with the handler's work and rolls back with it, including when the process dies before the
/// commit. The claim-and-release path cannot survive that, because its claim is already committed.
/// </para>
/// </summary>
public interface ITransactionalDeduplicationStore
{
    /// <summary>
    /// Claim <paramref name="deduplicationId" /> inside <paramref name="transaction" />. Returns
    /// <see langword="true" /> when this transaction now holds the claim, <see langword="false" /> when the id
    /// was already claimed.
    /// </summary>
    /// <remarks>
    /// The claim is an INSERT arbitrated by the table's primary key, so a concurrent caller holding an
    /// uncommitted claim for the same id makes this call WAIT for that transaction: a commit makes this one a
    /// duplicate, a rollback lets it through. Nothing is written outside <paramref name="transaction" />.
    /// </remarks>
    Task<bool> TryClaimAsync(DbConnection connection, DbTransaction? transaction, string deduplicationId,
        DateTimeOffset expires, CancellationToken cancellation = default);
}
