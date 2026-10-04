using JasperFx.Core.Reflection;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.Sagas;

/// <summary>
///     GH-4805. What generated saga code calls on a relational store, in place of
///     <see cref="SagaSupport{TId,TSaga}" />. The caller supplies the factory for the store's closed
///     schema type, which is the whole point: the construction is written out as source text and compiled
///     by ILC, so nothing has to dispatch a generic virtual or close a generic reflectively.
/// </summary>
/// <remarks>
///     Deliberately a sibling of <see cref="SagaSupport{TId,TSaga}" /> rather than a replacement. A store
///     that does not implement <see cref="ISagaSchemaSupplier" /> keeps the original path, and the
///     original path keeps working for every non-generated caller.
/// </remarks>
public static class RelationalSagaStorage
{
    /// <summary>
    ///     Resolve saga storage from the context's message store, building the schema with
    ///     <paramref name="factory" /> on a cache miss.
    /// </summary>
    public static ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(
        MessageContext context,
        Func<SagaTableDefinition, DatabaseSettings, IDatabaseSagaSchema<TId, TSaga>> factory) where TSaga : Saga
    {
        if (context.Storage is ISagaSchemaSupplier supplier)
        {
            return supplier.EnrollAndFetchSagaStorage(context, factory);
        }

        // The message store was swapped for one that cannot take a schema factory -- an ancillary store,
        // or a multi-tenanted routing decision -- after the chain was generated against one that could.
        // Fall back rather than fail: the original path reaches the same schema through SagaSchemaFor,
        // and only loses the AOT-safety this overload exists for.
        if (context.Storage is ISagaSupport fallback)
        {
            return fallback.EnrollAndFetchSagaStorage<TId, TSaga>(context);
        }

        throw new InvalidOperationException(
            $"The message store ({context.Storage}) for this application does not implement {typeof(ISagaSupport).FullNameInCode()}, so it cannot store saga state for {typeof(TSaga).FullNameInCode()}.");
    }
}
