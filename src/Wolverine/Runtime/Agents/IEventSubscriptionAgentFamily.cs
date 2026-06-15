namespace Wolverine.Runtime.Agents;

/// <summary>
/// Store-agnostic abstraction over the event-store agent family (implemented by Wolverine.Marten and
/// Wolverine.Polecat). Lets monitoring/admin tooling (CritterWatch) resolve the REAL live agent URI
/// for a projection/subscription shard without coupling to a specific event-store provider and
/// without hand-composing the agent URI — the URI grammar stays inside the provider that owns it.
/// </summary>
public interface IEventSubscriptionAgentFamily
{
    /// <summary>
    /// Resolve the live agent <see cref="Uri" /> for the shard identified by
    /// <paramref name="shardIdentity" /> (the JasperFx <c>ShardName.Identity</c>, e.g. "Trip:All")
    /// and optional <paramref name="tenantId" />. Returns null when no matching live agent is found
    /// in this family. CritterWatch resolves every registered family and uses the first non-null hit.
    /// </summary>
    ValueTask<Uri?> FindAgentUriAsync(string shardIdentity, string? tenantId, CancellationToken token = default);
}
