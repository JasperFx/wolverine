using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Wolverine.Attributes;
using Wolverine.Configuration;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// GH-4635. Refuses a handler, HTTP endpoint or gRPC service whose only route to a
/// <see cref="DbContext" /> is an <see cref="IDbContextFactory{TContext}" />.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EFCorePersistenceFrameProvider.CanApply" /> looks for a chain dependency that
/// <c>CanBeCastTo&lt;DbContext&gt;()</c>. A factory — <c>AddDbContextFactory</c>,
/// <c>AddPooledDbContextFactory</c>, or a hand-registered <c>PooledDbContextFactory&lt;T&gt;</c> — is not
/// one, so every piece of EF Core middleware declined and the chain compiled, started and ran with no
/// transaction, no <c>SaveChangesAsync</c> and no outbox. Nothing said so: not codegen, not startup, not
/// the generated source, not a log line. A handler in that shape looks exactly like a working one right
/// up until a failure after the save leaves its cascading messages already sent.
/// </para>
/// <para>
/// Supporting the shape is a design change (Wolverine would have to own the lifetime of a context it did
/// not create, and decide which of several factory-made contexts the outbox enlists in), so this refuses
/// it instead. The refusal is deliberately narrow: it only fires when the chain has NO
/// <see cref="DbContext" /> dependency of its own, which is exactly the silent case. A chain that injects
/// both keeps its middleware on the injected context, and the factory is then an explicit second
/// connection the author has clearly asked for.
/// </para>
/// <para>
/// An <see cref="IChainPolicy" /> rather than an <see cref="IHandlerPolicy" /> for the same reason as
/// <c>AncillaryStorageByAssemblyPolicy</c>: it is the only policy kind all three graphs apply, and a gRPC
/// service never runs attribute application at all.
/// </para>
/// </remarks>
internal class DbContextFactoryRefusalPolicy : IChainPolicy
{
    public void Apply(IReadOnlyList<IChain> chains, GenerationRules rules, IServiceContainer container)
    {
        foreach (var chain in chains)
        {
            // [NonTransactional] already means "I am not asking Wolverine to manage this chain's
            // transaction", which is precisely the acknowledgement being demanded here
            if (chain.HasAttribute<NonTransactionalAttribute>()) continue;

            var dependencies = chain.ServiceDependencies(container, Type.EmptyTypes).ToArray();

            // The middleware CAN apply, so nothing is silent -- leave it alone
            if (dependencies.Any(x => x.CanBeCastTo<DbContext>())) continue;

            var factoryTypes = dependencies
                .Select(closedFactoryInterface)
                .Where(x => x != null)
                .Select(x => x!.GetGenericArguments()[0])
                .Distinct()
                .ToArray();

            if (factoryTypes.Length == 0) continue;

            throw new InvalidOperationException(messageFor(chain, factoryTypes));
        }
    }

    private static string messageFor(IChain chain, IReadOnlyList<Type> contextTypes)
    {
        var factories = contextTypes
            .Select(x => $"{nameof(IDbContextFactory<DbContext>)}<{x.FullNameInCode()}>")
            .Join(", ");

        var first = contextTypes[0];

        return
            $"{chain.Description} depends on {factories}, but Wolverine's Entity Framework Core middleware " +
            $"only applies to a {nameof(DbContext)} resolved as a dependency of the chain itself. A " +
            $"{nameof(DbContext)} the handler creates from a factory gets no transaction, no automatic " +
            "SaveChangesAsync and no outbox -- cascading messages are sent immediately instead of being " +
            "enrolled, so a failure after the write leaves them already delivered. Wolverine will not " +
            $"silently apply nothing. Either take {first.FullNameInCode()} itself as a parameter (register " +
            $"it with AddDbContextWithWolverineIntegration<{first.Name}>() so the envelope storage is " +
            "mapped into its model), or put [NonTransactional] on it to say that this chain manages its " +
            "own DbContext lifetime and transaction.";
    }

    /// <summary>
    /// The closed <see cref="IDbContextFactory{TContext}" /> a dependency implements, or null. Matching the
    /// interface rather than the concrete type is what catches <c>PooledDbContextFactory&lt;T&gt;</c> and
    /// any hand-rolled factory alongside EF's own.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2067",
        Justification = "type comes from chain service-dependency discovery and is rooted by the handler or endpoint registration that named it. FindInterfaceThatCloses only inspects the generic-interface graph for IDbContextFactory<>. See AOT guide / #2755.")]
    private static Type? closedFactoryInterface(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDbContextFactory<>)) return type;

        return type.FindInterfaceThatCloses(typeof(IDbContextFactory<>));
    }
}
