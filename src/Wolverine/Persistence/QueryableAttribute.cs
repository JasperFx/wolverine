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
/// Injects the persistence mechanism's raw <see cref="IQueryable{T}" /> — Marten's
/// <c>session.Query&lt;T&gt;()</c>, EF Core's <c>dbContext.Set&lt;T&gt;()</c>, and so on — into a message
/// handler, HTTP endpoint, or middleware method parameter.
/// </summary>
/// <example>
/// <code>
/// [WolverineGet("/api/alerts/recent")]
/// public static Task&lt;List&lt;Alert&gt;&gt; GetRecent([Queryable] IQueryable&lt;Alert&gt; alerts, CancellationToken token)
///     => alerts.Where(x =&gt; x.Level == "high").OrderByDescending(x =&gt; x.RaisedAt).Take(20).ToListAsync(token);
/// </code>
/// </example>
/// <remarks>
/// <para>
/// <b>This is the escape hatch, and it is a sharp one.</b> Unlike <see cref="EntityAttribute" />,
/// <see cref="FirstOrDefaultAttribute" /> and <see cref="AllAttribute" />, which describe *what* you want and
/// leave the store to satisfy it, this hands you a provider-specific LINQ implementation. Read the warnings in
/// the documentation before reaching for it:
/// </para>
/// <list type="bullet">
/// <item>
/// It is <b>not portable in practice</b>. Marten, EF Core, RavenDb and CosmosDb LINQ providers support very
/// different subsets of LINQ; a query that compiles and runs on one can throw at *runtime* on another. The
/// type is storage agnostic, the query you write against it is not.
/// </item>
/// <item>
/// It reintroduces exactly the persistence coupling the other attributes exist to remove, and makes the method
/// meaningfully harder to unit test.
/// </item>
/// <item>
/// An unbounded query is easy to write by accident. There is no paging, no limit, and no guard.
/// </item>
/// <item>
/// On <b>CosmosDb</b> in particular, Wolverine stores every user document in one shared container with no
/// per-type discriminator, so an unfiltered queryable can surface documents of other types entirely.
/// </item>
/// </list>
/// <para>
/// Prefer <see cref="AllAttribute" /> for a whole small collection, <see cref="EntityAttribute" /> for a single
/// entity by identity, or <see cref="FromQuerySpecificationAttribute" /> / a compiled query for anything
/// filtered that you want to stay testable and portable.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class QueryableAttribute : WolverineParameterAttribute
{
    public QueryableAttribute()
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
                $"Could not determine a matching persistence service for [Queryable] parameter " +
                $"'{parameter.Name}' of element type {elementType.FullNameInCode()}. Check that the persistence " +
                "integration for this type has been registered, i.e. IntegrateWithWolverine() for Marten.");
        }

        if (!provider.TryBuildQueryableFrame(elementType, container, out var frame, out var result))
        {
            throw new InvalidOperationException(
                $"The {provider.GetType().FullNameInCode()} persistence provider does not support [Queryable], " +
                $"so parameter '{parameter.Name}' of element type {elementType.FullNameInCode()} cannot be " +
                "resolved.");
        }

        chain.Middleware.Add(frame);
        result.OverrideName(parameter.Name!);

        EntityAttribute.StoreDeferredMiddlewareVariable(chain, parameter.Name!, result);

        return result;
    }

    /// <summary>
    ///     The element type behind an <c>IQueryable&lt;T&gt;</c> parameter.
    /// </summary>
    internal static Type DetermineElementType(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IQueryable<>))
        {
            return type.GetGenericArguments()[0];
        }

        var member = parameter.Member;

        throw new InvalidOperationException(
            $"The [Queryable] attribute can only be applied to a parameter of type IQueryable<T>, but " +
            $"'{parameter.Name}' on {member.DeclaringType?.FullNameInCode()}.{member.Name} is declared as " +
            $"{type.FullNameInCode()}. Change it to IQueryable<{elementNameHint(type)}>.");
    }

    // Mirrors AllAttribute.elementNameHint -- deliberately avoids walking the interface graph, which would
    // need a DynamicallyAccessedMembers annotation the caller cannot satisfy for an exception message hint.
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
