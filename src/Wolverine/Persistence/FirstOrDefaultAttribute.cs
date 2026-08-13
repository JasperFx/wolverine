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
/// Marks a message handler or HTTP endpoint parameter as the first row of its type in the configured
/// persistence — the equivalent of <c>await session.Query&lt;T&gt;().FirstOrDefaultAsync()</c>, resolved through
/// whichever persistence provider owns the type. The point is storage agnostic code: the same handler is valid
/// whether the store behind it is Marten, Polecat, Fisher, RavenDb, EF Core, or CosmosDb.
///
/// <para>
/// The canonical use is a singleton configuration document — a type your system stores exactly one of, with no
/// meaningful identity to look it up by, which is precisely the case <see cref="EntityAttribute"/> cannot express
/// because it requires an identity value.
/// </para>
/// </summary>
/// <example>
/// <code>
/// [WolverineGet("/api/alerts/config/metrics/defaults")]
/// public static MetricsAlertDefaults GetMetricsDefaults([FirstOrDefault] MetricsAlertDefaults? defaults)
///     => defaults ?? new MetricsAlertDefaults();
/// </code>
/// </example>
/// <remarks>
/// <para>
/// The parameter is simply <c>null</c> when nothing matches, and the handler or endpoint runs either way. There is
/// deliberately no <c>Required</c> / <c>OnMissing</c> here: unlike <see cref="EntityAttribute"/>, a miss is not an
/// error condition worth a 404, it is an ordinary answer to "is there one of these yet?". Write your own null
/// branch — usually a <c>?? new T()</c> fallback.
/// </para>
/// <para>
/// The query is unfiltered on purpose. If you need a predicate, that is what a <c>Before</c> method, a compiled
/// query, or <see cref="FromQuerySpecificationAttribute"/> are for; ordering across six different LINQ providers
/// is not something this attribute tries to promise.
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Parameter)]
public class FirstOrDefaultAttribute : WolverineParameterAttribute
{
    public FirstOrDefaultAttribute()
    {
        ValueSource = ValueSource.Anything;
    }

    public override Variable Modify(IChain chain, ParameterInfo parameter, IServiceContainer container,
        GenerationRules rules)
    {
        // Nullable reference annotations do not change the CLR type, but an explicitly nullable value type
        // parameter would, and reading the first row of a Nullable<T> is meaningless.
        var entityType = Nullable.GetUnderlyingType(parameter.ParameterType) ?? parameter.ParameterType;

        if (!rules.TryFindPersistenceFrameProvider(container, entityType, out var provider))
        {
            throw new InvalidOperationException(
                $"Could not determine a matching persistence service for [FirstOrDefault] parameter " +
                $"'{parameter.Name}' of type {entityType.FullNameInCode()} on {DescribeMember(parameter)}. " +
                "Check that the persistence integration for this type has been registered, i.e. " +
                "IntegrateWithWolverine() for Marten.");
        }

        if (!provider.TryBuildFirstOrDefaultFrame(entityType, container, out var frame, out var result))
        {
            throw new InvalidOperationException(
                $"The {provider.GetType().FullNameInCode()} persistence provider does not support " +
                $"[FirstOrDefault], so parameter '{parameter.Name}' of type {entityType.FullNameInCode()} on " +
                $"{DescribeMember(parameter)} cannot be resolved. Load the value explicitly in a Before method " +
                "instead.");
        }

        chain.Middleware.Add(frame);
        result.OverrideName(parameter.Name!);

        // Keeps the value reachable from Before/After middleware methods added later, the same way
        // [Entity] does.
        EntityAttribute.StoreDeferredMiddlewareVariable(chain, parameter.Name!, result);

        return result;
    }
}
