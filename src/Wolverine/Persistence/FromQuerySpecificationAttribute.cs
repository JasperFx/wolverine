using System.Diagnostics.CodeAnalysis;
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
/// Marks a handler or HTTP endpoint parameter as the result of executing a query
/// specification — any type recognized by one of Wolverine's registered persistence
/// providers (Marten <c>ICompiledQuery&lt;,&gt;</c> / <c>IQueryPlan&lt;&gt;</c>, or
/// Wolverine.EntityFrameworkCore <c>IQueryPlan&lt;TDbContext,TResult&gt;</c>).
///
/// <para>
/// Wolverine constructs the specification by matching its public constructor's
/// parameters (and any remaining public settable properties) against other variables
/// in scope — message members, route values, headers, claims — then executes it at
/// codegen time, batching with other batch-capable loads on the same handler when
/// the underlying persistence provider supports it.
/// </para>
/// <para>
/// Works uniformly across providers by dispatching through
/// <see cref="IPersistenceFrameProvider.TryBuildFetchSpecificationFrame"/>. The first
/// registered provider that recognizes the specification type builds the fetch frame.
/// </para>
/// </summary>
/// <example>
/// <code>
/// public static void Handle(
///     ApproveOrder cmd,
///     [FromQuerySpecification(typeof(ActiveOrderForCustomer))] Order? order,
///     [FromQuerySpecification(typeof(LineItemsForOrder))]     IReadOnlyList&lt;LineItem&gt; items)
/// {
///     // Wolverine constructed both specs from cmd's fields, batched them in one
///     // round-trip, and passed the materialized results to this handler.
/// }
/// </code>
/// </example>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromQuerySpecificationAttribute : WolverineParameterAttribute
{
    /// <summary>
    /// Create a new <see cref="FromQuerySpecificationAttribute"/>.
    /// </summary>
    /// <param name="specificationType">
    /// Concrete type recognized by one of the registered persistence providers.
    /// Must have a public constructor whose parameters (and/or settable properties)
    /// can be resolved from the handler's message / route / context.
    /// </param>
    public FromQuerySpecificationAttribute(Type specificationType)
    {
        SpecificationType = specificationType ?? throw new ArgumentNullException(nameof(specificationType));

        if (specificationType.IsInterface || specificationType.IsAbstract)
        {
            throw new ArgumentException(
                $"Specification type {specificationType.FullName} must be a concrete class.",
                nameof(specificationType));
        }

        ValueSource = ValueSource.Anything;
    }

    /// <summary>
    /// The specification type Wolverine will construct and execute.
    /// </summary>
    public Type SpecificationType { get; }

    // Modify is called at codegen time to construct the spec instance via
    // GetProperties + ChoosePublicConstructor (GetConstructors below). Spec
    // types are user-supplied via [FromQuerySpecification(typeof(MySpec))]
    // and statically rooted by the attribute argument; the AOT-clean Static
    // codegen path bakes the constructor/property resolution into pre-
    // generated code, so this reflective walk only fires under Dynamic
    // codegen (intentionally not AOT-clean per docs/guide/aot.md).
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Spec type from runtime-resolved SpecificationType; statically rooted via [FromQuerySpecification(typeof(...))]. Dynamic codegen path. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Spec type from runtime-resolved SpecificationType; statically rooted via [FromQuerySpecification(typeof(...))]. Dynamic codegen path. See AOT guide.")]
    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        var ctor = ChoosePublicConstructor(SpecificationType);
        var ctorParameters = ctor.GetParameters();

        var args = new Variable[ctorParameters.Length];
        for (var i = 0; i < ctorParameters.Length; i++)
        {
            var p = ctorParameters[i];
            if (!chain.TryFindVariable(p.Name!, ValueSource, p.ParameterType, out var found))
            {
                throw new InvalidOperationException(
                    $"Cannot resolve constructor parameter '{p.Name}' of type {p.ParameterType.FullNameInCode()} " +
                    $"on specification type {SpecificationType.FullNameInCode()}. Make sure the parameter name " +
                    "matches a message member, route value, header, or other variable in scope.");
            }
            args[i] = found;
        }

        // Match any remaining writable public properties by name + type. This is the
        // canonical pattern for Marten compiled queries, where parameters are declared
        // as properties rather than constructor arguments.
        var propertyAssignments = new List<(string, Variable)>();
        foreach (var prop in SpecificationType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            if (prop.GetSetMethod(nonPublic: false) is null) continue;
            if (ctorParameters.Any(cp => string.Equals(cp.Name, prop.Name, StringComparison.OrdinalIgnoreCase)))
                continue;

            if (chain.TryFindVariable(prop.Name, ValueSource, prop.PropertyType, out var source))
            {
                propertyAssignments.Add((prop.Name, source));
            }
        }

        var specVarName = $"{parameter.Name}_spec";
        var construct = new ConstructSpecificationFrame(SpecificationType, args, propertyAssignments.ToArray(), specVarName);
        chain.Middleware.Add(construct);

        // Dispatch to whichever persistence provider recognizes the spec type.
        foreach (var provider in rules.PersistenceProviders())
        {
            if (provider.TryBuildFetchSpecificationFrame(construct.Spec, container, out var frame, out var result))
            {
                chain.Middleware.Add(frame);
                result.OverrideName(parameter.Name!);
                return result;
            }
        }

        throw new InvalidOperationException(
            $"No registered persistence provider recognizes {SpecificationType.FullNameInCode()} as a query specification. " +
            "Did you forget to call IntegrateWithWolverine() (Marten) or UseEntityFrameworkCoreTransactions() (EF Core)?");
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Spec type from runtime-resolved SpecificationType; statically rooted via [FromQuerySpecification(typeof(...))]. Dynamic codegen path. See AOT guide.")]
    private static ConstructorInfo ChoosePublicConstructor(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0)
        {
            throw new InvalidOperationException(
                $"Specification type {type.FullNameInCode()} has no public constructors.");
        }

        if (ctors.Length == 1) return ctors[0];

        // Prefer the constructor with the most parameters.
        return ctors.OrderByDescending(c => c.GetParameters().Length).First();
    }
}

/// <summary>
/// Generic variant of <see cref="FromQuerySpecificationAttribute"/> for C# 11+
/// callers (targeting .NET 7 and newer). Equivalent to
/// <c>[FromQuerySpecification(typeof(TSpecification))]</c> but drops the
/// <c>typeof(...)</c> ceremony.
/// </summary>
/// <typeparam name="TSpecification">
/// Concrete type recognized by one of the registered persistence providers.
/// </typeparam>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromQuerySpecificationAttribute<TSpecification> : FromQuerySpecificationAttribute
{
    public FromQuerySpecificationAttribute() : base(typeof(TSpecification))
    {
    }
}
