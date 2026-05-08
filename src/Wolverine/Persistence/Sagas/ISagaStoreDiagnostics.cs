using JasperFx.Descriptors;

namespace Wolverine.Persistence.Sagas;

/// <summary>
/// Operator-facing read-only diagnostic surface for the saga storages
/// registered with this Wolverine application. Mirrors the shape that
/// CritterWatch (and any other monitoring tool) needs to render saga
/// state for an event-store explorer: list the saga types this service
/// knows about, fetch a single saga instance by type-name + identity,
/// and list recent instances for a given saga type.
/// </summary>
/// <remarks>
/// One implementation per registered saga storage (Marten, EF Core,
/// RavenDB, …). Wolverine wraps every registered implementation in an
/// internal aggregator and exposes that aggregator through
/// <see cref="Wolverine.Runtime.IWolverineRuntime.SagaStorage"/> so
/// callers always see one unified view, even when a host registers
/// multiple saga storages for different saga types.
/// </remarks>
public interface ISagaStoreDiagnostics
{
    /// <summary>
    /// Every saga type this storage knows about, with the messages that
    /// start vs continue each saga and the storage-provider tag (e.g.
    /// <c>Marten</c>, <c>EntityFrameworkCore</c>) used by monitoring tools
    /// to group sagas by their backing store.
    /// </summary>
    /// <param name="ct">Token to cancel the diagnostic call.</param>
    Task<IReadOnlyList<SagaTypeDescriptor>> GetRegisteredSagaTypesAsync(CancellationToken ct);

    /// <summary>
    /// Load a single saga instance by its type name and identity. Returns
    /// <c>null</c> when the saga type isn't owned by this storage, or
    /// when no instance with that identity exists.
    /// </summary>
    /// <param name="sagaTypeName">
    /// The saga type's full name in code (matches
    /// <see cref="SagaTypeDescriptor.SagaType"/>'s <c>Name</c>).
    /// </param>
    /// <param name="identity">
    /// The saga identity. Wolverine boxes whatever the saga's id member
    /// returns — Guid, int, long, string, or a strong-typed id — and the
    /// implementation is expected to coerce as needed.
    /// </param>
    /// <param name="ct">Token to cancel the diagnostic call.</param>
    Task<SagaInstanceState?> ReadSagaAsync(string sagaTypeName, object identity, CancellationToken ct);

    /// <summary>
    /// Return up to <paramref name="count"/> recent saga instances of
    /// type <paramref name="sagaTypeName"/>. "Recent" is provider-defined
    /// — typically ordered by last-modified-descending — but the contract
    /// is that this returns a bounded peek for monitoring UIs, never an
    /// unbounded scan. Returns an empty list when this storage does not
    /// own the requested saga type.
    /// </summary>
    /// <param name="sagaTypeName">The saga type's full name in code.</param>
    /// <param name="count">Maximum instances to return. Implementations
    /// may clamp to a sensible upper bound to protect the store.</param>
    /// <param name="ct">Token to cancel the diagnostic call.</param>
    Task<IReadOnlyList<SagaInstanceState>> ListSagaInstancesAsync(string sagaTypeName, int count, CancellationToken ct);
}
