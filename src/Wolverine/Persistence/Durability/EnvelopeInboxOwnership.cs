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
            // The main store or a tenant database of it, as it always has been
            if (store.Role != MessageStoreRole.Ancillary) return true;

            owner = mainStore;
        }

        // A multi-tenanted store keeps the row in the tenant's database, which is the one a non-ancillary
        // transaction was opened on
        if (owner is MultiTenantedMessageStore) return store.Role != MessageStoreRole.Ancillary;

        return ReferenceEquals(owner, store) || owner.Uri == store.Uri;
    }
}
