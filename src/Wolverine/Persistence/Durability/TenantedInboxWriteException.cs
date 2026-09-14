namespace Wolverine.Persistence.Durability;

/// <summary>
/// GH-4435. Thrown when storing incoming envelopes failed for at least one of the message stores behind
/// a <see cref="MultiTenantedMessageStore" />. Carries the envelopes that did NOT land, so a caller can
/// settle or retry exactly those, and whether the MAIN store was among the failures.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IncludesMainStore" /> is the listener pause decision. One tenant database being
/// unreachable must not pause a listener that serves every other tenant: the receiver falls back to its
/// per-envelope path, which defers the affected envelopes back to the broker and leaves everyone else
/// flowing. A failure that reached the main store is a different thing -- nothing can be persisted at
/// all -- and the listener pauses for inbox recovery exactly as it always has.
/// </para>
/// <para>
/// Note what this exception deliberately does NOT wrap: <see cref="DuplicateIncomingEnvelopeException" />.
/// The receiver's deduplication path keys off that exact type, so a duplicate is rethrown unchanged.
/// </para>
/// </remarks>
public class TenantedInboxWriteException : Exception
{
    public TenantedInboxWriteException(IReadOnlyList<Envelope> unpersisted, bool includesMainStore,
        IReadOnlyList<Exception> failures)
        : base(
            $"Failed to store {unpersisted.Count} incoming envelope(s) across {failures.Count} message store(s)",
            failures.Count == 1 ? failures[0] : new AggregateException(failures))
    {
        Unpersisted = unpersisted;
        IncludesMainStore = includesMainStore;
    }

    /// <summary>
    /// The envelopes that were not persisted. Envelopes belonging to a store whose write DID commit are
    /// absent, because each store's batch insert is all-or-nothing.
    /// </summary>
    public IReadOnlyList<Envelope> Unpersisted { get; }

    /// <summary>
    /// Was the main message store among the failures? When false, every failure was scoped to a tenant
    /// database and the listener should keep running.
    /// </summary>
    public bool IncludesMainStore { get; }
}
