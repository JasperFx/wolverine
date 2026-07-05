using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

/// <summary>
/// GH-3291: enrolls a Wolverine-enabled DbContext + the IMessageContext in the outgoing outbox
/// transaction WITHOUT beginning an explicit database transaction. Used for Wolverine.Http endpoints in
/// <see cref="Wolverine.Persistence.TransactionMiddlewareMode.Lightweight"/> mode.
///
/// Unlike a message handler — whose MessageContext is enlisted at runtime by
/// <c>MessageContext.ReadEnvelope</c> when it reads the incoming envelope — an HTTP endpoint has no
/// incoming envelope, so its <c>MessageContext.Transaction</c> is otherwise null. In Lightweight mode
/// that means <c>MessageBus.PersistOrSendAsync</c> takes the send-now branch and cascaded messages are
/// dispatched BEFORE the <c>SaveChangesAsync</c> postprocessor commits. Enlisting here makes those
/// cascades buffer and flush after the commit instead.
///
/// This deliberately does NOT call <c>BeginTransactionAsync</c> (that is what
/// <see cref="EnrollDbContextInTransaction"/> does for Eager mode): the write is covered by the implicit
/// transaction <c>SaveChangesAsync</c> opens, and skipping the explicit begin keeps this compatible with
/// EF Core's retrying execution strategy (<c>EnableRetryOnFailure</c>), which forbids user-initiated
/// transactions. Implements <see cref="IFlushesMessages"/> so <c>HttpChain</c> does not also add a
/// standalone (pre-commit) <c>FlushOutgoingMessages</c>; the paired
/// <see cref="CommitEfCoreEnvelopeTransaction"/> postprocessor performs the post-commit flush.
/// </summary>
internal class EnlistDbContextInOutbox : AsyncFrame, IFlushesMessages
{
    private readonly Type _dbContextType;
    private readonly Variable _envelopeTransaction;
    private Variable _dbContext = null!;
    private Variable? _context;
    private Variable _scrapers = null!;

    public EnlistDbContextInOutbox(Type dbContextType)
    {
        _dbContextType = dbContextType;
        _envelopeTransaction = new Variable(typeof(EfCoreEnvelopeTransaction), this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment(
            "GH-3291: enroll the DbContext & IMessagingContext in the outbox so cascaded messages buffer");
        writer.WriteComment(
            "and flush AFTER SaveChangesAsync commits. No explicit transaction is started (Lightweight mode).");
        writer.Write($"var {_envelopeTransaction.Usage} = new {typeof(EfCoreEnvelopeTransaction).FullNameInCode()}({_dbContext.Usage}, {_context!.Usage}, {_scrapers.Usage});");
        writer.Write($"await {_context.Usage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.Usage}).ConfigureAwait(false);");

        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Mirrors the C# body inside an async `task { }` computation expression: awaits become `do!`
        // and `.ConfigureAwait(false)` is dropped (the CE controls scheduling). See
        // EnrollDbContextInTransaction for the same conventions.
        writer.Write("");
        writer.WriteComment(
            "GH-3291: enroll the DbContext & IMessagingContext in the outbox (Lightweight mode, no explicit transaction)");
        writer.Write($"{_envelopeTransaction.FSharpAssignmentUsage} = {typeof(EfCoreEnvelopeTransaction).FSharpName()}({_dbContext.FSharpUsage}, {_context!.FSharpUsage}, {_scrapers.FSharpUsage})");
        writer.Write($"do! {_context.FSharpUsage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.FSharpUsage})");

        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _scrapers = chain.FindVariable(typeof(IEnumerable<IDomainEventScraper>));
        yield return _scrapers;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;
    }
}
