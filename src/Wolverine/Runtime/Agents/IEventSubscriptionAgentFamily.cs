namespace Wolverine.Runtime.Agents;

/// <summary>
/// Store-agnostic abstraction over the event-store agent family (implemented by Wolverine.Marten and
/// Wolverine.Polecat). Lets monitoring/admin tooling (CritterWatch) resolve the REAL agent URI for a
/// projection/subscription shard without coupling to a specific event-store provider and without
/// hand-composing the agent URI — the URI grammar stays inside the provider that owns it.
/// </summary>
public interface IEventSubscriptionAgentFamily
{
    /// <summary>
    /// Resolve the agent <see cref="Uri" /> for the shard identified by
    /// <paramref name="shardIdentity" /> (the JasperFx <c>ShardName.Identity</c>, e.g. "Trip:All")
    /// and optional <paramref name="tenantId" />. Resolution is based on the <em>registered</em>
    /// subscription set, so it succeeds regardless of whether the shard's agent is currently running —
    /// a paused, stopped, crashed, or not-yet-started projection still resolves, which is what lets an
    /// operator restart or rebuild a non-running projection (see GH-3124). Returns null when no matching
    /// registered shard is found in this family. CritterWatch resolves every registered family and uses
    /// the first non-null hit.
    /// </summary>
    ValueTask<Uri?> FindAgentUriAsync(string shardIdentity, string? tenantId, CancellationToken token = default);

    /// <summary>
    /// Same as <see cref="FindAgentUriAsync(string,string?,CancellationToken)" />, but scoped to a single event
    /// store rather than searching every store this family manages. <paramref name="storeIdentity" /> is the
    /// store's <c>EventStoreIdentity.ToString()</c> value (matched case-insensitively), which
    /// <see cref="EventSubscriptionAgentFamily.StoreIdentityOf" /> derives from an agent URI.
    ///
    /// <para>The store-blind overload concatenates the agents of every store and matches on the trailing shard
    /// path alone, so when the SAME projection name is registered in more than one store it returns whichever
    /// store happens to be enumerated first. Since this URI is what tooling uses to route restart / pause /
    /// resume / rebuild at a <em>live</em> agent, guessing wrong acts on another store's running agent. See
    /// GH-3647, and GH-3618 for the same gap on the transient-rebuild path.</para>
    ///
    /// <para>Returns <c>null</c> when this family does not manage <paramref name="storeIdentity" />, or when that
    /// store has no matching registered shard, so a caller looping over families can try the next one.</para>
    ///
    /// <para>The default implementation ignores <paramref name="storeIdentity" /> and delegates to the store-blind
    /// overload, preserving the behavior of any pre-existing implementation of this interface. Implementations
    /// that manage more than one store should override it.</para>
    /// </summary>
    ValueTask<Uri?> FindAgentUriAsync(string storeIdentity, string shardIdentity, string? tenantId,
        CancellationToken token = default)
        => FindAgentUriAsync(shardIdentity, tenantId, token);

    /// <summary>
    /// Rebuild a REGISTERED projection addressed by its <paramref name="shardIdentity" /> (the JasperFx
    /// <c>ShardName.Identity</c>, e.g. <c>"Trip:All"</c>) regardless of its lifecycle (Inline / Live /
    /// Async) or whether it is currently distributed as a continuous agent. This is the path for rebuilding
    /// a projection that has <em>no live agent</em> to route a rebuild through — an Inline or Live
    /// projection, or an async projection not presently distributed to any node. Wolverine resolves the
    /// projection from the registered subscription set, spins up a transient rebuild agent for it on the
    /// handling node, replays to the high-water mark, then stops it. A non-null <paramref name="tenantId" />
    /// scopes a per-tenant rebuild. Returns <c>true</c> when a registered projection matched and the rebuild
    /// ran; <c>false</c> when no registered shard matches (so the caller can try the next family). See
    /// GH-3163.
    /// </summary>
    ValueTask<bool> TryRebuildRegisteredProjectionAsync(string shardIdentity, string? tenantId,
        CancellationToken token = default);

    /// <summary>
    /// Same as <see cref="TryRebuildRegisteredProjectionAsync(string,string?,CancellationToken)" />, but scoped to a
    /// single event store rather than searching every store this family manages. <paramref name="storeIdentity" /> is
    /// the store's <c>EventStoreIdentity.ToString()</c> value (matched case-insensitively);
    /// <see cref="EventSubscriptionAgentFamily.StoreIdentityOf" /> derives it from an agent URI that
    /// <see cref="FindAgentUriAsync(string,string?,CancellationToken)" /> already resolved, so a caller never has to decompose the URI itself.
    ///
    /// <para>This exists because the store-blind overload cannot tell two stores apart when the SAME projection
    /// name is registered in more than one of them (a primary plus an ancillary store, say) and NEITHER has a live
    /// agent — the first store with a matching registered shard wins, which may not be the store the operator asked
    /// about. The live-agent rebuild path is already store-scoped downstream; this closes the gap on the last-resort
    /// transient-rebuild path. See GH-3618.</para>
    ///
    /// <para>Returns <c>true</c> when the named store matched a registered projection and the rebuild ran;
    /// <c>false</c> when this family does not manage <paramref name="storeIdentity" /> or that store has no matching
    /// registered shard, so the caller can try the next family.</para>
    ///
    /// <para>The default implementation ignores <paramref name="storeIdentity" /> and delegates to the store-blind
    /// overload, preserving the behavior of any pre-existing implementation of this interface. Implementations that
    /// manage more than one store should override it.</para>
    /// </summary>
    ValueTask<bool> TryRebuildRegisteredProjectionAsync(string storeIdentity, string shardIdentity, string? tenantId,
        CancellationToken token = default)
        => TryRebuildRegisteredProjectionAsync(shardIdentity, tenantId, token);
}
