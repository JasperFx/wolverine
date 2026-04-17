using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Linq;

namespace Wolverine.Marten.Codegen;

/// <summary>
/// Method pre-compilation policy that detects when a frame produces a variable
/// whose type is a Marten query "specification" — <see cref="ICompiledQuery{TDoc,TOut}"/>
/// or <see cref="IQueryPlan{T}"/> (or <see cref="IBatchQueryPlan{T}"/>) — and
/// injects a <see cref="FetchSpecificationFrame"/> to execute it and produce
/// the materialized result as a downstream-consumable variable.
///
/// <para>
/// This enables the user ergonomic of returning a spec instance directly from a
/// <c>Load()</c> / <c>LoadAsync()</c> method, possibly as part of a tuple,
/// without wrapping in a marker type:
/// </para>
///
/// <code>
/// public static OrderByIdCompiled LoadOrder(ApproveOrder cmd)
///     =&gt; new OrderByIdCompiled(cmd.OrderId);
///
/// public static LineItemsForOrder LoadItems(ApproveOrder cmd)
///     =&gt; new LineItemsForOrder(cmd.OrderId);
///
/// public static void Handle(ApproveOrder cmd, Order order, IReadOnlyList&lt;LineItem&gt; items)
///     =&gt; ... // Wolverine codegen has fetched both specs, batched where possible
/// </code>
///
/// <para>
/// Runs BEFORE <see cref="MartenBatchingPolicy"/> so that injected
/// <see cref="FetchSpecificationFrame"/>s (which are <see cref="IBatchableFrame"/>)
/// are picked up and grouped into a single <see cref="Marten.Services.BatchQuerying.IBatchedQuery"/>.
/// </para>
/// </summary>
internal class QuerySpecificationPolicy : IMethodPreCompilationPolicy
{
    public void Apply(IGeneratedMethod method)
    {
        // Snapshot existing frames first — we'll mutate method.Frames as we go.
        var frames = method.Frames.ToList();

        foreach (var frame in frames)
        {
            // Skip our own injected machinery — ConstructSpecificationFrame is
            // already paired with a FetchSpecificationFrame by
            // FromQuerySpecificationAttribute; both are invisible to this policy.
            if (frame is FetchSpecificationFrame) continue;
            if (frame is ConstructSpecificationFrame) continue;

            // Find all Create-d variables whose type is a query specification.
            // A single frame may produce multiple specs (ValueTuple unpacking from
            // a Load method that returns e.g. (OrderByIdCompiled, LineItemsForOrder)).
            var specVars = frame.Creates.Where(v => IsSpecification(v.VariableType)).ToList();
            if (specVars.Count == 0) continue;

            // Insert a FetchSpecificationFrame immediately after the frame that
            // produced the spec var. Iterate in reverse so indexes stay stable.
            var baseIndex = method.Frames.IndexOf(frame);
            foreach (var specVar in specVars)
            {
                var fetchFrame = new FetchSpecificationFrame(specVar);
                method.Frames.Insert(baseIndex + 1, fetchFrame);
            }
        }
    }

    /// <summary>
    /// Returns true if <paramref name="type"/> implements any of the Marten
    /// specification contracts (compiled query, query plan, or batch query plan).
    /// Guards against false positives by requiring the closure to be on Marten's
    /// own namespaces — a user type that happens to implement a custom
    /// <c>IQueryPlan&lt;T&gt;</c> from a different library will not match.
    /// </summary>
    private static bool IsSpecification(Type type)
    {
        if (type == null) return false;

        // ICompiledQuery<TDoc, TResult>
        var compiled = type.FindInterfaceThatCloses(typeof(ICompiledQuery<,>));
        if (compiled is not null && compiled.Namespace == typeof(ICompiledQuery<,>).Namespace)
        {
            return true;
        }

        var batchPlan = type.FindInterfaceThatCloses(typeof(IBatchQueryPlan<>));
        if (batchPlan is not null && batchPlan.Namespace == typeof(IBatchQueryPlan<>).Namespace)
        {
            return true;
        }

        var queryPlan = type.FindInterfaceThatCloses(typeof(IQueryPlan<>));
        if (queryPlan is not null && queryPlan.Namespace == typeof(IQueryPlan<>).Namespace)
        {
            return true;
        }

        return false;
    }
}
