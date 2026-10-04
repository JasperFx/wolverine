using Wolverine.Persistence.Sagas;
using Wolverine.Runtime;

namespace Wolverine.RDBMS.Sagas;

/// <summary>
///     GH-4805. The Native AOT-safe path to a saga's storage: generated code hands in a factory that
///     constructs the closed schema type, instead of the store reaching it through a generic virtual.
/// </summary>
/// <remarks>
///     <para><b>Why this exists.</b> <c>MessageDatabase&lt;T&gt;.SagaSchemaFor&lt;TSaga, TId&gt;()</c> is
///     <c>abstract</c> — a generic method declared on a generic class and overridden per store — which
///     makes it a <i>generic virtual method</i>. NativeAOT cannot synthesize a GVM implementation for an
///     instantiation it did not precompile, and it does not precompile this one even when the whole call
///     path is statically named by generated code. The measured failure is a hard <c>FailFast</c>:
///     <c>Failed to create generic virtual method implementation / SagaSchemaFor / AotSaga, System.Guid</c>.
///     Rooting cannot fix it, and the declaring store type is <c>internal</c>, so no application or
///     generated code can force the instantiation either.</para>
///
///     <para><b>Why a factory works.</b> The one thing that has to happen is constructing the store's
///     closed schema type. Generated code knows both type arguments statically, so it can write that
///     construction as source text — ILC then compiles it like any other call, with no
///     <c>MakeGenericType</c> and no virtual generic dispatch. The method below is a generic method on a
///     <i>non-generic interface</i>, which is the shape that was measured to resolve correctly: the
///     existing <see cref="ISagaSupport.EnrollAndFetchSagaStorage{TId,TSaga}" /> has exactly that shape
///     and ILC resolves it in the same native image where <c>SagaSchemaFor</c> fails.</para>
///
///     <para><b>Additive on purpose.</b> This is a new interface rather than a member on
///     <see cref="ISagaSupport" />, and <c>SagaSchemaFor</c> is left exactly as it was — it is still what
///     the non-generated path and the store test suites call. Adding to a shipped interface breaks
///     implementors at runtime (see the JasperFx.Events precedent), and nothing here needs that.</para>
/// </remarks>
public interface ISagaSchemaSupplier
{
    /// <summary>
    ///     Open a connection and transaction, enlist the context's outbox in it, and return saga storage
    ///     over the schema the supplied factory builds.
    /// </summary>
    /// <param name="context">The active message context, enlisted in the outbox.</param>
    /// <param name="factory">
    ///     Builds the store's closed schema type. Called only on a cache miss, so a <c>static</c> lambda
    ///     from generated code allocates nothing in steady state.
    /// </param>
    ValueTask<ISagaStorage<TId, TSaga>> EnrollAndFetchSagaStorage<TId, TSaga>(
        MessageContext context,
        Func<SagaTableDefinition, DatabaseSettings, IDatabaseSagaSchema<TId, TSaga>> factory)
        where TSaga : Saga;
}
