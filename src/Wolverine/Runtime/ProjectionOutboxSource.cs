using Microsoft.Extensions.DependencyInjection;
using Wolverine.Persistence.Durability;

namespace Wolverine.Runtime;

/// <summary>
///     The store agnostic half of a projection outbox (a store's <c>IMessageOutbox.CreateBatch</c>):
///     builds the batch's <see cref="MessageContext" /> for the session's tenant, points it at the
///     owning store's message store, enlists it in the session and hands back the
///     <see cref="ProjectionSideEffectSink" /> the store's <c>IMessageBatch</c> delegates to.
/// </summary>
/// <remarks>
///     Marten, Polecat and Fisher each kept their own copy of this, and the copies drifted: only
///     Marten's took the ancillary store type and overrode the storage (GH-2887), so a projection side
///     effect from an ancillary Polecat or Fisher store enlisted its envelopes against the MAIN message
///     store. GH-5143.
/// </remarks>
internal sealed class ProjectionOutboxSource
{
    private readonly Lazy<IWolverineRuntime> _runtime;

    /// <param name="services">The application container, resolved lazily because the store's options are
    /// configured before the Wolverine runtime exists.</param>
    /// <param name="storeType">
    ///     The ancillary store's marker type, or null for the main store. When set, envelope writes go to
    ///     that store's own message store (its own schema or database), not the runtime's default store.
    /// </param>
    public ProjectionOutboxSource(IServiceProvider services, Type? storeType)
    {
        _runtime = new Lazy<IWolverineRuntime>(services.GetRequiredService<IWolverineRuntime>);
        StoreType = storeType;
    }

    public Type? StoreType { get; }

    /// <summary>
    ///     Start one projection batch for a session.
    /// </summary>
    /// <param name="tenantId">The session's tenant id.</param>
    /// <param name="transactionSource">Builds the store specific envelope transaction over the session.</param>
    public async ValueTask<ProjectionSideEffectSink> StartBatchAsync(string tenantId,
        Func<MessageContext, IEnvelopeTransaction> transactionSource)
    {
        var runtime = _runtime.Value;
        var context = new MessageContext(runtime, tenantId)
        {
            MultiFlushMode = MultiFlushMode.AllowMultiples
        };

        if (StoreType is not null)
        {
            context.OverrideStorage(runtime.FindAncillaryStoreForMarkerType(StoreType));
        }

        await context.EnlistInOutboxAsync(transactionSource(context)).ConfigureAwait(false);

        return new ProjectionSideEffectSink(context, transactionSource);
    }
}
