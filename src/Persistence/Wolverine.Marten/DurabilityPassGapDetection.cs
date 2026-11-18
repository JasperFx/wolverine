using Marten;
using Wolverine.Postgresql;

namespace Wolverine.Marten;

/// <summary>
///     GH-5620. Under <see cref="DurabilitySettings.UseTransactionScopedDurabilityPassLock" /> the durability pass holds
///     its advisory lock in a session that sits idle in an open transaction for the whole pass. Marten's gap detection
///     counts any such session as a possible reserver of an event sequence gap, so a pass step that waits for
///     asynchronous projections to catch up waited on its own pass's lock until the pass ran out of time. Marten 10
///     (marten#5791) can be told to ignore the holders of a lock key; it matches the full 64-bit key and still never
///     ignores a session that has written anything, so registering the key is safe whether or not the
///     transaction-scoped lock is on.
/// </summary>
internal static class DurabilityPassGapDetection
{
    /// <summary>
    ///     Register the durability pass lock of the Wolverine message store in <paramref name="schemaName" /> with this
    ///     Marten store's gap detection. One registration covers every tenant database, since their message stores use
    ///     the same schema and so the same key. Done while the store is configured, before any daemon can poll
    /// </summary>
    public static void Register(StoreOptions options, string? schemaName)
    {
        options.Projections.IgnoreAdvisoryLockHoldersForGapDetection(
            PostgresqlMessageStore.DurabilityPassLockIdFor(schemaName ?? "public"));
    }

    /// <summary>
    ///     Mark the message store whose lock this Marten store's gap detection ignores, so the durability pass does not
    ///     warn about it. A store whose schema was not registered (the store's schema changed after Wolverine
    ///     configured it) keeps the warning: registering it now could race a daemon that is already polling
    /// </summary>
    public static void MarkIfRegistered(StoreOptions options, PostgresqlMessageStore messageStore)
    {
        if (options.Projections.IgnoredAdvisoryLockKeysForGapDetection.Contains(messageStore.DurabilityPassLockId))
        {
            messageStore.DurabilityPassLockIgnoredByGapDetection = true;
        }
    }
}
