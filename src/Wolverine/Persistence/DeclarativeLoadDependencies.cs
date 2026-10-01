using JasperFx;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx.CodeGeneration.Frames;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Middleware;

namespace Wolverine.Persistence;

/// <summary>
/// GH-4717. The persistence a chain implies through its LOAD ATTRIBUTES, which
/// <see cref="Chain.ServiceDependencies" /> structurally cannot see.
/// </summary>
/// <remarks>
/// <para>
/// Every provider's <c>CanApply</c> ultimately answers from <c>ServiceDependencies</c>, which walks only
/// <c>Middleware.OfType&lt;MethodCall&gt;()</c> plus the handler calls — method parameters and constructor
/// dependencies. Wolverine's own load attributes inject non-<c>MethodCall</c> frames that resolve their store
/// through <c>IMethodVariables.FindVariable</c> at code-generation time, so the store is a local in the
/// generated method and never a chain dependency. The policy and the frames look at two different things that
/// never meet.
/// </para>
/// <para>
/// On a message handler there is a second, independent miss: the attributes' <c>Modify()</c> does not run until
/// <c>HandlerChain.applyCustomizations</c>, long after <c>AutoApplyTransactions</c> has already asked. Which is
/// why this is keyed off the ATTRIBUTES on the handler and step method parameters rather than off the frames they inject —
/// the attributes are readable on both orderings, and the frames are not.
/// </para>
/// <para>
/// GH-4712 was this gap costing an EF Core user their writes. It is latent rather than harmless on the document
/// stores only because Wolverine's Marten sessions are <c>DocumentTracking.None</c>, so mutating a loaded
/// document was never going to persist and those users already return a storage action. Any provider with a
/// change tracker turns the same gap into silent data loss.
/// </para>
/// </remarks>
public static class DeclarativeLoadDependencies
{
    /// <summary>
    /// The entity types this chain loads declaratively — through <c>[Entity]</c> and its derivatives,
    /// <c>[All]</c>, <c>[FirstOrDefault]</c>, <c>[Queryable]</c> or <c>[FromQuerySpecification]</c>.
    /// </summary>
    public static IEnumerable<Type> DeclarativelyLoadedEntityTypes(this IChain chain)
    {
        foreach (var method in methodsThatMayLoad(chain))
        {
            foreach (var parameter in method.GetParameters())
            {
                if (parameter.GetCustomAttributes().Any(isLoadAttribute))
                {
                    yield return candidateEntityType(parameter.ParameterType);
                }
            }
        }
    }

    /// <summary>
    /// Every method whose parameters can carry a load attribute: the handler calls, the middleware method
    /// calls already on the chain, and the handler types' own <c>Before</c> / <c>Validate</c> / <c>Load</c>
    /// methods. An <c>[Entity]</c> on a step's parameter loads through the store exactly as one on the handler
    /// method does, so a chain that only loads there must still be claimed -- otherwise its change-tracked
    /// writes are silently dropped.
    /// </summary>
    /// <remarks>
    /// The handler types' step methods are read off the TYPE rather than off <c>Middleware</c> because
    /// <c>[Transactional]</c> is applied before <c>ApplyImpliedMiddlewareFromHandlers</c> has added them, on
    /// both message handlers and HTTP endpoints.
    /// </remarks>
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Handler-type method walk for Before/Validate/Load methods at codegen time; handler types statically rooted via HandlerDiscovery. Same pattern as Chain.ApplyImpliedMiddlewareFromHandlers.")]
    private static IEnumerable<MethodInfo> methodsThatMayLoad(IChain chain)
    {
        var handlerCalls = chain.HandlerCalls();
        var methods = handlerCalls.Select(x => x.Method)
            .Concat(chain.Middleware.OfType<MethodCall>().Select(x => x.Method))
            .ToList();

        foreach (var handlerType in handlerCalls.Select(x => x.HandlerType).Distinct())
        {
            methods.AddRange(MiddlewarePolicy.FilterMethods<WolverineBeforeAttribute>(chain, handlerType.GetMethods(),
                MiddlewarePolicy.BeforeMethodNames));
        }

        return methods.Distinct();
    }

    // FromEfCoreAttribute derives from ExplicitEntityAttribute, which derives from EntityAttribute, so the
    // first test covers the whole [Entity] family including any provider's own explicit form.
    private static bool isLoadAttribute(Attribute attribute)
    {
        return attribute is EntityAttribute
            or AllAttribute
            or FirstOrDefaultAttribute
            or QueryableAttribute
            or FromQuerySpecificationAttribute;
    }

    /// <summary>
    /// The entity type a load attribute is asking for. The attributes' own <c>DetermineElementType</c> helpers
    /// throw on a malformed parameter, which is right when they are building a frame and wrong here — this
    /// answers a question rather than failing a bootstrap. A shape not recognised falls through to the
    /// parameter type itself and simply fails to match any provider.
    /// </summary>
    private static Type candidateEntityType(Type parameterType)
    {
        // [All] and [FromQuerySpecification] take IReadOnlyList<T>; [Queryable] takes IQueryable<T>
        if (parameterType.IsGenericType)
        {
            var definition = parameterType.GetGenericTypeDefinition();
            if (definition == typeof(IReadOnlyList<>) || definition == typeof(IQueryable<>))
            {
                return parameterType.GetGenericArguments()[0];
            }
        }

        return parameterType;
    }

    /// <summary>
    /// The providers that claim this chain's transaction: the ones whose <c>CanApply</c> says so, and — only
    /// when that finds nothing at all — the ones that can persist an entity the chain loads declaratively.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The fallback is deliberately consulted ONLY when <c>CanApply</c> claimed nothing, so no chain that
    /// resolves an owner today can have that answer changed by this. That matters more than it looks: with two
    /// claimants and no <c>[Storage]</c> / <c>[Transactional(typeof(X))]</c> designation,
    /// <see cref="TransactionOwnerResolution.SelectDesignatedOwner" /> throws, so a broader rule would convert
    /// a latent bug in mixed-store applications into a hard bootstrap failure for people it never affected.
    /// </para>
    /// <para>
    /// Within the fallback, a selective provider wins outright over a catch-all. <c>CanPersist</c> on a
    /// catch-all store claims every type it is shown, so without this an <c>[Entity]</c> chain in an
    /// application registering both EF Core and Marten would come back with two claimants and no way to choose
    /// — which is the same hard failure by another route. This is what <see cref="IPersistenceFrameProvider.IsCatchAll" />
    /// is for, and it is the same precedence <c>OrderedPersistenceProviders</c> already applies.
    /// </para>
    /// </remarks>
    internal static List<IPersistenceFrameProvider> ProvidersClaiming(
        this IReadOnlyList<IPersistenceFrameProvider> ordered, IChain chain, IServiceContainer container)
    {
        var claimants = ordered.Where(x => x.CanApply(chain, container)).ToList();
        if (claimants.Count != 0)
        {
            return claimants;
        }

        var entityTypes = chain.DeclarativelyLoadedEntityTypes().Distinct().ToArray();
        if (entityTypes.Length == 0)
        {
            return claimants;
        }

        bool persistsAny(IPersistenceFrameProvider provider)
        {
            return entityTypes.Any(type => provider.CanPersist(type, container, out _));
        }

        var selective = ordered.Where(x => !x.IsCatchAll).Where(persistsAny).ToList();

        return selective.Count != 0
            ? selective
            : ordered.Where(x => x.IsCatchAll).Where(persistsAny).ToList();
    }
}
