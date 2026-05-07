using Wolverine.Persistence.Durability;

namespace Wolverine.Runtime;

/// <summary>
/// Convenience surface on top of <see cref="IMessageStore.Listeners"/> for
/// registering, removing, and listing the dynamic-listener URIs at runtime
/// (GH-2685). All three calls delegate straight through to the underlying
/// <see cref="IListenerStore"/> — they exist purely so consumers don't have
/// to reach through <c>runtime.Storage.Listeners</c> in user code.
///
/// Registration is durable: once persisted, the listener URI is picked up by
/// <see cref="Agents.DynamicListenerAgentFamily"/> on the cluster's next
/// assignment cycle (default 30s) and activated on whichever node the
/// cluster picks. Removal is also durable: the assigned node stops the
/// listener within one polling interval. Both operations are idempotent.
///
/// Requires <see cref="DurabilitySettings.EnableDynamicListeners"/> to be set
/// at host configuration time. When the flag is off these methods are still
/// callable but they hit <see cref="NullListenerStore"/>: register/remove
/// no-op and the all-listeners list is always empty.
/// </summary>
public static class WolverineRuntimeListenerExtensions
{
    /// <summary>
    /// Persist <paramref name="listenerUri"/> as a registered listener that the
    /// cluster will activate on one node. Idempotent — repeat registrations of
    /// the same URI are no-ops.
    /// </summary>
    public static Task RegisterListenerAsync(this IWolverineRuntime runtime, Uri listenerUri,
        CancellationToken cancellationToken = default)
    {
        if (runtime is null) throw new ArgumentNullException(nameof(runtime));
        if (listenerUri is null) throw new ArgumentNullException(nameof(listenerUri));

        return runtime.Storage.Listeners.RegisterListenerAsync(listenerUri, cancellationToken);
    }

    /// <summary>
    /// Remove <paramref name="listenerUri"/> from the registry. Within one
    /// cluster assignment cycle (default 30s) the assigned node stops the
    /// listener. Idempotent — removing an unregistered URI is a no-op.
    /// </summary>
    public static Task RemoveListenerAsync(this IWolverineRuntime runtime, Uri listenerUri,
        CancellationToken cancellationToken = default)
    {
        if (runtime is null) throw new ArgumentNullException(nameof(runtime));
        if (listenerUri is null) throw new ArgumentNullException(nameof(listenerUri));

        return runtime.Storage.Listeners.RemoveListenerAsync(listenerUri, cancellationToken);
    }

    /// <summary>
    /// Snapshot of every currently-registered listener URI. Order is
    /// implementation-defined.
    /// </summary>
    public static Task<IReadOnlyList<Uri>> AllRegisteredListenersAsync(this IWolverineRuntime runtime,
        CancellationToken cancellationToken = default)
    {
        if (runtime is null) throw new ArgumentNullException(nameof(runtime));
        return runtime.Storage.Listeners.AllListenersAsync(cancellationToken);
    }
}
