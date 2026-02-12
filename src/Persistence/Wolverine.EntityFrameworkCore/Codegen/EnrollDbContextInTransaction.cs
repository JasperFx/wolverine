using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.EntityFrameworkCore.Internals;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.EntityFrameworkCore.Codegen;

internal class EnrollDbContextInTransaction : AsyncFrame
{
    private readonly Type _dbContextType;
    private readonly IdempotencyStyle _idempotencyStyle;
    private Variable _dbContext;
    private Variable _cancellation;
    private Variable _envelopeTransaction;
    private Variable? _context;
    private Variable _scrapers;

    public EnrollDbContextInTransaction(Type dbContextType, IdempotencyStyle idempotencyStyle)
    {
        _dbContextType = dbContextType;
        _idempotencyStyle = idempotencyStyle;

        _envelopeTransaction = new Variable(typeof(EfCoreEnvelopeTransaction), this);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment(
            "Enroll the DbContext & IMessagingContext in the outgoing Wolverine outbox transaction");
        writer.Write($"var {_envelopeTransaction.Usage} = new {typeof(EfCoreEnvelopeTransaction).FullNameInCode()}({_dbContext.Usage}, {_context.Usage}, {_scrapers.Usage});");
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
        
        Next?.GenerateCode(method, writer);
        
        writer.Write($"await {_envelopeTransaction.Usage}.CommitAsync({_cancellation.Usage}).ConfigureAwait(false);");
        writer.FinishBlock();
        writer.Write($"BLOCK:catch ({typeof(Exception).FullNameInCode()})");
        writer.Write($"await {_envelopeTransaction.Usage}.RollbackAsync().ConfigureAwait(false);");
        writer.Write("throw;");
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