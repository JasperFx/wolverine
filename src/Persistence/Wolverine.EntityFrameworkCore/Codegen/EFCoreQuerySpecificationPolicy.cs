using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// Method pre-compilation policy that detects when a frame produces a variable
/// whose type is a Wolverine.EntityFrameworkCore
/// <see cref="IQueryPlan{TDbContext,TResult}"/> (or batch variant) and injects
/// a <see cref="FetchSpecificationFrame"/> to execute it. Enables the user
/// ergonomic of returning a spec directly from a <c>Load()</c> method
/// (single or tuple).
///
/// <para>
/// Runs BEFORE <see cref="EFCoreBatchingPolicy"/> so the injected
/// <see cref="FetchSpecificationFrame"/>s (which are
/// <see cref="IEFCoreBatchableFrame"/>) are grouped into a shared
/// <see cref="Weasel.EntityFrameworkCore.Batching.BatchedQuery"/>.
/// </para>
/// </summary>
internal class EFCoreQuerySpecificationPolicy : IMethodPreCompilationPolicy
{
    public void Apply(IGeneratedMethod method)
    {
        // Snapshot existing frames — we'll mutate method.Frames as we go.
        var frames = method.Frames.ToList();

        foreach (var frame in frames)
        {
            // Skip our own injected machinery — ConstructSpecificationFrame is
            // paired with a FetchSpecificationFrame by FromQuerySpecificationAttribute.
            if (frame is FetchSpecificationFrame) continue;
            if (frame is ConstructSpecificationFrame) continue;

            // Find all Create-d variables whose type is an EF Core spec.
            // A single frame may produce multiple specs (ValueTuple unpacking from
            // a Load method that returns e.g. (OrdersByCustomerPlan, DiscountPlan)).
            var specVars = frame.Creates.Where(v => IsEfCoreSpecification(v.VariableType)).ToList();
            if (specVars.Count == 0) continue;

            var baseIndex = method.Frames.IndexOf(frame);
            foreach (var specVar in specVars)
            {
                var fetchFrame = new FetchSpecificationFrame(specVar);
                method.Frames.Insert(baseIndex + 1, fetchFrame);
            }
        }
    }

    /// <summary>
    /// True if <paramref name="type"/> implements
    /// <see cref="IQueryPlan{TDbContext,TResult}"/> or
    /// <see cref="IBatchQueryPlan{TDbContext,TResult}"/> from THIS assembly's
    /// namespace. Namespace guard prevents false positives from user types
    /// that happen to implement a similarly-named interface from another library.
    /// </summary>
    private static bool IsEfCoreSpecification(Type type)
    {
        if (type == null) return false;

        var batchPlan = type.FindInterfaceThatCloses(typeof(IBatchQueryPlan<,>));
        if (batchPlan is not null && batchPlan.Namespace == typeof(IBatchQueryPlan<,>).Namespace)
        {
            return true;
        }

        var queryPlan = type.FindInterfaceThatCloses(typeof(IQueryPlan<,>));
        if (queryPlan is not null && queryPlan.Namespace == typeof(IQueryPlan<,>).Namespace)
        {
            return true;
        }

        return false;
    }
}
