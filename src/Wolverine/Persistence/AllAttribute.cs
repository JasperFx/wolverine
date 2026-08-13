using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.Sagas;

namespace Wolverine.Persistence;

/// <summary>
/// Marks a message handler or HTTP endpoint parameter as <b>every</b> document of its element type in the
/// configured persistence — the equivalent of <c>await session.Query&lt;T&gt;().ToListAsync()</c>, resolved
/// through whichever persistence provider owns the type. Like <see cref="FirstOrDefaultAttribute"/>, the point
/// is storage agnostic code: the same handler is valid whether the store behind it is Marten, Polecat, Fisher,
/// RavenDb, or EF Core.
/// </summary>
/// <example>
/// <code>
/// [WolverineGet("/api/alerts/config/services")]
/// public static IReadOnlyList&lt;ServiceAlertOverrides&gt; GetAll([All] IReadOnlyList&lt;ServiceAlertOverrides&gt; overrides)
///     => overrides;
/// </code>
/// </example>
/// <remarks>
/// <para>
/// The parameter must be declared as <c>IReadOnlyList&lt;T&gt;</c>. Anything else throws with a message naming
/// the parameter and what it should have been.
/// </para>
/// <para>
/// An empty table yields an empty list, never null, so there is no "missing" case to configure and no
/// <c>Required</c> / <c>OnMissing</c> here.
/// </para>
/// <para>
/// The query is unfiltered on purpose. If you need a predicate, that is what a <c>Before</c> method, a compiled
/// query, or <see cref="FromQuerySpecificationAttribute"/> are for. Reading an entire table into memory is also
/// something to do deliberately — this is aimed at small reference and configuration collections.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class AllAttribute : WolverineParameterAttribute
{
    public AllAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        var elementType = DetermineElementType(parameter);

        if (!rules.TryFindPersistenceFrameProvider(container, elementType, out var provider))
        {
            throw new InvalidOperationException(
                $"Could not determine a matching persistence service for [All] parameter '{parameter.Name}' of " +
                $"element type {elementType.FullNameInCode()} on {DescribeMember(parameter)}. Check that the " +
                "persistence integration for this type has been registered, i.e. IntegrateWithWolverine() for " +
                "Marten.");
        }

        if (!provider.TryBuildAllFrame(elementType, container, out var frame, out var result))
        {
            throw new InvalidOperationException(
                $"The {provider.GetType().FullNameInCode()} persistence provider does not support [All], so " +
                $"parameter '{parameter.Name}' of element type {elementType.FullNameInCode()} on " +
                $"{DescribeMember(parameter)} cannot be resolved. Load the values explicitly in a Before method " +
                "instead.");
        }

        chain.Middleware.Add(frame);
        result.OverrideName(parameter.Name!);

        // Keeps the value reachable from Before/After middleware methods added later, the same way
        // [Entity] and [FirstOrDefault] do.
        EntityAttribute.StoreDeferredMiddlewareVariable(chain, parameter.Name!, result);

        return result;
    }

    /// <summary>
    ///     The element type behind an <c>IReadOnlyList&lt;T&gt;</c> parameter.
    /// </summary>
    /// <remarks>
    ///     Deliberately one accepted shape rather than any <c>IEnumerable&lt;T&gt;</c>. <c>IReadOnlyList&lt;T&gt;</c>
    ///     is what Marten and RavenDb hand back from <c>ToListAsync()</c> natively, and EF Core's
    ///     <c>List&lt;T&gt;</c> converts to it implicitly, so every provider assigns straight across with no
    ///     copying. A lazy <c>IEnumerable&lt;T&gt;</c> would also leave the reader guessing whether the query had
    ///     already run.
    /// </remarks>
    internal static Type DetermineElementType(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IReadOnlyList<>))
        {
            return type.GetGenericArguments()[0];
        }

        throw new InvalidOperationException(
            $"The [All] attribute can only be applied to a parameter of type IReadOnlyList<T>, but " +
            $"'{parameter.Name}' on {DescribeMember(parameter)} is declared as " +
            $"{type.FullNameInCode()}. Change it to IReadOnlyList<{elementNameHint(type)}>.");
    }

    // Best effort so the message can suggest the concrete fix rather than a bare "List<T>". Deliberately
    // avoids walking the interface graph -- that needs a DynamicallyAccessedMembers annotation the caller
    // cannot satisfy, and this is only a hint inside an exception message.
    private static string elementNameHint(Type type)
    {
        if (type.IsArray)
        {
            return type.GetElementType()!.NameInCode();
        }

        if (type.IsGenericType && type.GetGenericArguments().Length == 1)
        {
            return type.GetGenericArguments()[0].NameInCode();
        }

        return type.NameInCode();
    }
}
