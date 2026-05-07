namespace Wolverine.Persistence.Durability;

/// <summary>
/// Persistence contract for the dynamic-listener registry. Stores the set of
/// listener URIs Wolverine should activate at runtime in addition to the
/// listeners declared statically through <c>WolverineOptions</c>. The registry
/// is opt-in via <see cref="DurabilitySettings.EnableDynamicListeners"/> — when
/// disabled, providers must NOT create the backing storage so that users
/// upgrading Wolverine see no schema migration churn.
///
/// The store is transport-agnostic: each entry is a single <see cref="Uri"/>.
/// The pluggable <c>DynamicListenerAgentFamily</c> turns each persisted URI
/// into a runtime listener via the appropriate transport.
/// </summary>
public interface IListenerStore
{
    /// <summary>
    /// Persist <paramref name="uri"/> as a registered listener. Idempotent — a
    /// repeat registration of the same URI is a no-op rather than an error.
    /// </summary>
    Task RegisterListenerAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove <paramref name="uri"/> from the registry. Idempotent — removing a
    /// URI that isn't registered is a no-op rather than an error.
    /// </summary>
    Task RemoveListenerAsync(Uri uri, CancellationToken cancellationToken = default);

    /// <summary>
    /// Snapshot of every currently-registered listener URI. The order is
    /// implementation-defined; callers that need a deterministic ordering should
    /// sort the result themselves.
    /// </summary>
    Task<IReadOnlyList<Uri>> AllListenersAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Default no-op listener store. Returned by <see cref="IMessageStore.Listeners"/>
/// when dynamic listeners are disabled (<see cref="DurabilitySettings.EnableDynamicListeners"/>
/// is <c>false</c>) or when the message store has no durable backing (e.g. <c>NullMessageStore</c>
/// in solo-mode-without-DB scenarios). All operations are no-ops or return empty.
/// </summary>
public sealed class NullListenerStore : IListenerStore
{
    public static NullListenerStore Instance { get; } = new();

    public Task RegisterListenerAsync(Uri uri, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task RemoveListenerAsync(Uri uri, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<Uri>> AllListenersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<Uri>>(Array.Empty<Uri>());
}
