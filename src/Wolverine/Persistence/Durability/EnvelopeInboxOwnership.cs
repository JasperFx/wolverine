namespace Wolverine.Persistence.Durability;

internal static class EnvelopeInboxOwnership
{
    /// <summary>
    ///     GH-5040 / GH-5045. Can a handler's transaction against <paramref name="store" /> mark this envelope's
    ///     inbox row as handled? Only when that store's database is the one holding the row. The row lives in
    ///     <see cref="Envelope.Store" /> when a receiver routed it to an ancillary store, and otherwise in the
    ///     main store -- including every row a database-backed queue moved into the inbox itself on dequeue.
    ///     <para>
    ///     When this is false, a commit listener must neither write the mark-handled statement nor flip
    ///     <see cref="Envelope.Status" /> to <see cref="EnvelopeStatus.Handled" />: the statement would match no
    ///     rows in the wrong database, and the flipped status makes <c>DurableReceiver</c> skip the mark that
    ///     would have reached the right one, so the row stays Incoming and is replayed on every restart.
    ///     </para>
    /// </summary>
    public static bool InboxRowIsIn(this Envelope envelope, IMessageStore store, IMessageStore mainStore)
    {
        var owner = envelope.Store;
        if (owner == null)
        {
            // The row is in the main store, or the tenant database of it the transaction was opened on
            if (store.Role == MessageStoreRole.Main || ReferenceEquals(store, mainStore)) return true;

            // GH-5732: a tenant database is not necessarily the MAIN store's. One of an ancillary store that
            // registers tenants has role Tenant too, and only the main store can say whether it is one of its own
            owner = mainStore;
        }

        // A multi-tenanted store keeps the row in the tenant's database, which is the one a transaction for that
        // tenant was opened on -- but only when the transaction's database belongs to that store at all. GH-5731:
        // a handler on another ancillary store's tenant database (role Tenant, not Ancillary) used to pass here,
        // mark the row on the wrong database, and flip the status that tells DurableReceiver to mark it itself
        if (owner is MultiTenantedMessageStore multiTenanted)
        {
            if (store is MultiTenantedMessageStore) return ReferenceEquals(store, owner);
            return multiTenanted.Holds(store);
        }

        return ReferenceEquals(owner, store) || owner.Uri == store.Uri;
    }
}
