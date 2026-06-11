using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

internal class EnrollDbContextInTransaction : AsyncFrame, IFlushesMessages
{
    private readonly Type _dbContextType;
    private readonly IdempotencyStyle _idempotencyStyle;
    private Variable _dbContext = null!;
    private Variable _cancellation = null!;
    private Variable _envelopeTransaction;
    private Variable? _context;
    private Variable _scrapers = null!;

    public EnrollDbContextInTransaction(Type dbContextType, IdempotencyStyle idempotencyStyle)
    {
        _dbContextType = dbContextType;
        _idempotencyStyle = idempotencyStyle;

        _envelopeTransaction = new Variable(typeof(EfCoreEnvelopeTransaction), this);
    }

    public Type DbContextType => _dbContextType;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment(
            "Enroll the DbContext & IMessagingContext in the outgoing Wolverine outbox transaction");
        writer.Write($"var {_envelopeTransaction.Usage} = new {typeof(EfCoreEnvelopeTransaction).FullNameInCode()}({_dbContext.Usage}, {_context!.Usage}, {_scrapers.Usage});");
        writer.Write(
            $"await {_context.Usage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.Usage}).ConfigureAwait(false);");


        writer.WriteComment("Start the actual database transaction if one does not already exist");
        writer.Write($"BLOCK:if ({_dbContext.Usage}.Database.CurrentTransaction == null)");
        writer.Write($"await {_dbContext.Usage}.Database.BeginTransactionAsync({_cancellation.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();
        writer.Write("BLOCK:try");

        // EF Core can only do eager idempotent checks
        if (_idempotencyStyle == IdempotencyStyle.Eager || _idempotencyStyle == IdempotencyStyle.Optimistic)
        {
            writer.Write($"await {_context.Usage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation.Usage}).ConfigureAwait(false);");
        }
        
        // The commit + outbox flush is NOT emitted here anymore. It is emitted by the
        // CommitEfCoreEnvelopeTransaction postprocessor (added by EFCorePersistenceFrameProvider),
        // which is part of Next and runs BEFORE the HTTP response writer - while still inside this
        // try/catch, so a failed commit rolls back and never produces a success response. This is
        // what lets Wolverine's transactional outbox flush (and thus the TrackActivity "sent"
        // bookkeeping) complete before the response is written. See GH-2917.
        Next?.GenerateCode(method, writer);

        writer.FinishBlock();
        writer.Write($"BLOCK:catch ({typeof(Exception).FullNameInCode()})");
        writer.Write($"await {_envelopeTransaction.Usage}.RollbackAsync().ConfigureAwait(false);");
        writer.Write("throw;");
        writer.FinishBlock();
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        // This middleware only ever runs inside an async handler/endpoint, so the body is a `task { }`
        // computation expression: awaits are `do!` (or `let! _ =` when a result must be discarded),
        // and `.ConfigureAwait(false)` is dropped (the CE controls scheduling).
        writer.Write("");
        writer.WriteComment(
            "Enroll the DbContext & IMessagingContext in the outgoing Wolverine outbox transaction");
        writer.Write($"{_envelopeTransaction.FSharpAssignmentUsage} = {typeof(EfCoreEnvelopeTransaction).FSharpName()}({_dbContext.FSharpUsage}, {_context!.FSharpUsage}, {_scrapers.FSharpUsage})");
        writer.Write($"do! {_context.FSharpUsage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.FSharpUsage})");

        writer.WriteComment("Start the actual database transaction if one does not already exist");
        // F# has no `== null`; use `isNull`. BeginTransactionAsync returns Task<IDbContextTransaction>,
        // so bind-and-discard with `let! _ =` (then `()` makes the then-branch unit, as `if/then` requires).
        writer.Write($"BLOCK:if isNull {_dbContext.FSharpUsage}.Database.CurrentTransaction then");
        writer.Write($"let! _ = {_dbContext.FSharpUsage}.Database.BeginTransactionAsync({_cancellation.FSharpUsage})");
        writer.Write("()");
        writer.FinishBlock();

        writer.Write("BLOCK:try");

        // EF Core can only do eager idempotent checks
        if (_idempotencyStyle == IdempotencyStyle.Eager || _idempotencyStyle == IdempotencyStyle.Optimistic)
        {
            writer.Write($"do! {_context.FSharpUsage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation.FSharpUsage})");
        }

        // See the C# overload for why the commit/flush lives in Next (the CommitEfCoreEnvelopeTransaction
        // postprocessor) rather than here. GH-2917.
        Next?.GenerateFSharpCode(method, writer);

        writer.FinishBlock();
        // F# exception handler: roll back, then rethrow. `reraise()` is illegal inside a computation
        // expression's try/with (FS0413), so use ExceptionDispatchInfo to preserve the original stack
        // trace — the exact semantics of C# `throw;`.
        writer.Write("BLOCK:with ex ->");
        writer.Write($"do! {_envelopeTransaction.FSharpUsage}.RollbackAsync()");
        writer.Write($"{typeof(System.Runtime.ExceptionServices.ExceptionDispatchInfo).FSharpName()}.Capture(ex).Throw()");
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _scrapers = chain.FindVariable(typeof(IEnumerable<IDomainEventScraper>));
        yield return _scrapers;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}

/// <summary>
/// Commits the Ef Core envelope transaction (committing the EF Core database transaction and then
/// flushing the MessageContext's outgoing messages). Emitted as a postprocessor so it runs before
/// the HTTP response writer, ensuring the outbox is flushed before the response is sent (GH-2917).
/// Pairs with <see cref="EnrollDbContextInTransaction" />, which begins the transaction and provides
/// the try/catch (and the <see cref="EfCoreEnvelopeTransaction" /> variable this frame commits).
/// </summary>
internal class CommitEfCoreEnvelopeTransaction : AsyncFrame
{
    private Variable _envelopeTransaction = null!;
    private Variable _cancellation = null!;

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(
            "Commit the EF Core transaction and flush outgoing messages before writing the response (GH-2917)");
        writer.Write($"await {_envelopeTransaction.Usage}.CommitAsync({_cancellation.Usage}).ConfigureAwait(false);");
        Next?.GenerateCode(method, writer);
    }

    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(
            "Commit the EF Core transaction and flush outgoing messages before writing the response (GH-2917)");
        writer.Write($"do! {_envelopeTransaction.FSharpUsage}.CommitAsync({_cancellation.FSharpUsage})");
        Next?.GenerateFSharpCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelopeTransaction = chain.FindVariable(typeof(EfCoreEnvelopeTransaction));
        yield return _envelopeTransaction;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}