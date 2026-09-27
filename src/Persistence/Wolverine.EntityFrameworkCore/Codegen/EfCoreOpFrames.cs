using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Configuration;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
///     Code generation for the <see cref="EfCoreOp" /> side effect family (GH-4629).
/// </summary>
// AOT note (#2746): the same chunk P pattern as EFCorePersistenceFrameProvider next door. Every
// reflective call here runs at CODEGEN time over DbContext types that their own DI registration
// statically roots; AOT-clean applications run pre-generated frames in TypeLoadMode.Static and
// never reach this class at all.
[UnconditionalSuppressMessage("Trimming", "IL2026",
    Justification = "EF Core codegen -- runs at codegen time over DbContext types that are statically rooted by their own DI registration. See AOT guide.")]
[UnconditionalSuppressMessage("Trimming", "IL2067",
    Justification = "EF Core codegen -- service types walked from the container's own registrations. See AOT guide.")]
[UnconditionalSuppressMessage("AOT", "IL3050",
    Justification = "EF Core codegen -- IDbContextBuilder<> closed over a registered DbContext type at codegen time. See AOT guide.")]
internal static class EfCoreOpFrames
{
    internal static Frame Build(IChain chain, Variable variable, GenerationRules rules, IServiceContainer container)
    {
        var provider = rules.PersistenceProviders().OfType<EFCorePersistenceFrameProvider>().FirstOrDefault()
                       ?? throw new InvalidOperationException(
                           $"{chain.Description} returns an {nameof(EfCoreOp)}, but EF Core persistence is not registered. Call UseEntityFrameworkCoreTransactions() in your Wolverine configuration.");

        var dbContextType = DetermineDbContextType(chain, container, provider);

        // The op is applied against the DbContext that owns this chain's transaction, so the chain
        // depends on that DbContext whether or not the handler signature says so -- the whole point of
        // the declarative form is that the handler need not accept one. Declaring the dependency here
        // is also what lets ApplyTransactionSupport below resolve the same type we just resolved.
        chain.AddDependencyType(dbContextType);

        provider.ApplyTransactionSupport(chain, container);
        chain.IsTransactional = true;

        return new ExecuteEfCoreOpFrame(variable, dbContextType).WrapIfNotNull(variable);
    }

    /// <summary>
    ///     True when this chain must run in <see cref="TransactionMiddlewareMode.Eager" /> no matter what
    ///     the application, the attribute or a policy asked for, because something in it writes outside
    ///     the <c>SaveChangesAsync</c> that carries the outbox.
    /// </summary>
    internal static bool RequiresEagerTransaction(IChain chain)
    {
        foreach (var returned in chain.ReturnVariablesOfType<EfCoreOp>())
        {
            // An abstract declared return type -- the EfCoreOp base itself, which is how the operations
            // read best in a handler signature -- tells codegen nothing about which operation comes
            // back, so assume the one that needs the transaction.
            if (returned.VariableType.IsAbstract) return true;

            if (returned.VariableType.CanBeCastTo<IBypassesSaveChanges>()) return true;
        }

        foreach (var call in chain.HandlerCalls())
        {
            if (call.Method.GetCustomAttribute<RequiresEagerTransactionAttribute>() != null) return true;
            if (call.HandlerType.GetCustomAttribute<RequiresEagerTransactionAttribute>() != null) return true;
        }

        return false;
    }

    /// <summary>
    ///     The <c>DbContext</c> an <see cref="EfCoreOp" /> returned by this chain runs against.
    /// </summary>
    /// <remarks>
    ///     A handler that returns an operation instead of taking a <c>DbContext</c> parameter has no
    ///     <c>DbContext</c> among its service dependencies for
    ///     <c>EFCorePersistenceFrameProvider.DetermineDbContextType</c> to find, which is the point of the
    ///     declarative form. When the application registered exactly one <c>DbContext</c> there is nothing
    ///     to decide; when it registered several, say which with <c>[Storage(typeof(YourDbContext))]</c>
    ///     rather than have Wolverine guess.
    /// </remarks>
    internal static Type DetermineDbContextType(IChain chain, IServiceContainer container,
        EFCorePersistenceFrameProvider provider)
    {
        try
        {
            return provider.DetermineDbContextType(chain, container);
        }
        catch (Exception)
        {
            // Falls through to the single-registration case below, whose error message is the useful
            // one for a handler that never mentions a DbContext at all.
        }

        var candidates = RegisteredDbContextTypes(container);

        if (candidates.Length == 1) return candidates[0];

        if (candidates.Length == 0)
        {
            throw new InvalidOperationException(
                $"{chain.Description} returns an {nameof(EfCoreOp)}, but no DbContext is registered with Wolverine. Register one with services.AddDbContextWithWolverineIntegration<YourDbContext>(/* ... */).");
        }

        throw new InvalidOperationException(
            $"{chain.Description} returns an {nameof(EfCoreOp)}, and this application registers more than one DbContext ({candidates.Select(x => x.Name).Join(", ")}). Designate the one that owns the operation with [Storage(typeof(YourDbContext))] or [Transactional(typeof(YourDbContext))] on the handler.");
    }

    internal static Type[] RegisteredDbContextTypes(IServiceContainer container)
    {
        var direct = container.FindMatchingServices(type => type.CanBeCastTo<DbContext>())
            .Select(x => x.ServiceType);

        var tenanted = container.FindMatchingServices(type => type.Closes(typeof(IDbContextBuilder<>)))
            .Select(x => x.ServiceType.GetGenericArguments()[0]);

        return direct.Concat(tenanted).Distinct().ToArray();
    }

}

/// <summary>
///     Applies one <see cref="EfCoreOp" /> returned by a handler against the chain's DbContext.
/// </summary>
internal class ExecuteEfCoreOpFrame : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly Variable _op;
    private Variable _cancellation = null!;
    private Variable _dbContext = null!;

    public ExecuteEfCoreOpFrame(Variable op, Type dbContextType)
    {
        _op = op;
        _dbContextType = dbContextType;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _op;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("Apply the EF Core operation returned by the handler");
        writer.Write(
            $"await {_op.Usage}.{nameof(EfCoreOp.ExecuteAsync)}({_dbContext.Usage}, {_cancellation.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }
}
