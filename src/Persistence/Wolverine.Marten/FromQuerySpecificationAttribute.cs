using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Marten;
using Marten.Linq;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Marten.Codegen;

namespace Wolverine.Marten;

/// <summary>
/// Marks a handler or HTTP endpoint parameter as the result of executing a Marten
/// query specification — either an <see cref="ICompiledQuery{TDoc,TOut}"/> or an
/// <see cref="IQueryPlan{T}"/>. Wolverine constructs the specification by matching
/// its constructor parameters against other variables in scope (message members,
/// route values, headers, claims) and executes it at codegen time, batching with
/// other batch-capable loads on the same handler when possible.
///
/// <para>
/// Use this attribute when you want a specification-driven load tied directly
/// to the handler signature without writing a <c>Load()</c> method. When you
/// need complex construction logic, prefer returning the specification instance
/// directly from a <c>Load()</c> method — Wolverine will detect and execute it
/// the same way.
/// </para>
/// </summary>
/// <example>
/// <code>
/// public static void Handle(
///     ApproveOrder cmd,
///     [FromQuerySpecification(typeof(OrderByIdCompiled))] Order order,
///     [FromQuerySpecification(typeof(LineItemsForOrder))] IReadOnlyList&lt;LineItem&gt; items)
/// {
///     // Wolverine has built both specs from cmd's fields, batched them, and
///     // passed the materialized results to this handler.
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
    /// Concrete type implementing either <see cref="ICompiledQuery{TDoc,TOut}"/>
    /// or <see cref="IQueryPlan{T}"/>. Must have a public constructor whose
    /// parameters can be resolved from the handler's message / route / context.
    /// </param>
    public FromQuerySpecificationAttribute(Type specificationType)
    {
        SpecificationType = specificationType ?? throw new ArgumentNullException(nameof(specificationType));

        if (!IsValidSpecification(specificationType))
        {
            throw new ArgumentException(
                $"Type {specificationType.FullName} does not implement Marten's ICompiledQuery<,>, IQueryPlan<>, or IBatchQueryPlan<>.",
                nameof(specificationType));
        }

        ValueSource = ValueSource.Anything;
    }

    /// <summary>
    /// The specification type Wolverine will construct and execute.
    /// </summary>
    public Type SpecificationType { get; }

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

        // Marten compiled queries typically declare their parameters as public
        // settable properties rather than constructor arguments. Resolve any
        // such property whose name matches a variable in scope and emit an
        // assignment after construction. Writable instance properties only —
        // static and read-only properties are left alone.
        var propertyAssignments = new List<(string, Variable)>();
        foreach (var prop in SpecificationType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanWrite) continue;
            if (prop.GetSetMethod(nonPublic: false) is null) continue;
            // Skip properties already satisfied by constructor args (matched by name)
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

        var fetch = new FetchSpecificationFrame(construct.Spec);
        chain.Middleware.Add(fetch);

        fetch.Result.OverrideName(parameter.Name!);
        return fetch.Result;
    }

    private static ConstructorInfo ChoosePublicConstructor(Type type)
    {
        var ctors = type.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        if (ctors.Length == 0)
        {
            throw new InvalidOperationException(
                $"Specification type {type.FullNameInCode()} has no public constructors.");
        }

        if (ctors.Length == 1) return ctors[0];

        // Prefer the constructor with the most parameters — matches typical
        // "primary constructor with data, optional helper ctor for serialization"
        // patterns commonly seen in compiled-query / plan classes.
        return ctors.OrderByDescending(c => c.GetParameters().Length).First();
    }

    private static bool IsValidSpecification(Type type)
    {
        if (type.IsInterface || type.IsAbstract) return false;

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

/// <summary>
/// Generic variant of <see cref="FromQuerySpecificationAttribute"/> for C# 11+
/// callers (targeting .NET 7 and newer). Equivalent to
/// <c>[FromQuerySpecification(typeof(TSpecification))]</c> but avoids the
/// <c>typeof(...)</c> ceremony.
/// </summary>
/// <typeparam name="TSpecification">
/// Concrete type implementing either <see cref="ICompiledQuery{TDoc,TOut}"/>
/// or <see cref="IQueryPlan{T}"/>.
/// </typeparam>
[AttributeUsage(AttributeTargets.Parameter)]
public class FromQuerySpecificationAttribute<TSpecification> : FromQuerySpecificationAttribute
{
    public FromQuerySpecificationAttribute() : base(typeof(TSpecification))
    {
    }
}
