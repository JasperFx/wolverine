using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.EntityFrameworkCore.Storage;
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

    public EnrollDbContextInTransaction(Type dbContextType, IdempotencyStyle idempotencyStyle)
    {
        _dbContextType = dbContextType;
        _idempotencyStyle = idempotencyStyle;

        Transaction = new Variable(typeof(IDbContextTransaction), $"tx_{_dbContextType.NameInCode().Sanitize()}", this);
        _envelopeTransaction = new Variable(typeof(EfCoreEnvelopeTransaction), this);
    }

    public Variable Transaction { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine("");
        writer.WriteComment(
            "Enroll the DbContext & IMessagingContext in the outgoing Wolverine outbox transaction");
        writer.Write($"var {_envelopeTransaction.Usage} = new {typeof(EfCoreEnvelopeTransaction).FullNameInCode()}({_dbContext.Usage}, {_context.Usage});");
        writer.Write(
            $"await {_context.Usage}.{nameof(MessageContext.EnlistInOutboxAsync)}({_envelopeTransaction.Usage});");

        
        writer.WriteComment("Start the actual database transaction");
        writer.Write($"using var {Transaction.Usage} = await {_dbContext.Usage}.Database.BeginTransactionAsync({_cancellation.Usage});");
        writer.Write("BLOCK:try");

        if (_idempotencyStyle == IdempotencyStyle.Eager)
        {
            writer.Write($"await {_context.Usage}.{nameof(MessageContext.AssertEagerIdempotencyAsync)}({_cancellation.Usage});");
        }
        
        Next?.GenerateCode(method, writer);
        
        writer.Write($"await {_envelopeTransaction.Usage}.CommitAsync({_cancellation.Usage});");
        writer.FinishBlock();
        writer.Write($"BLOCK:catch ({typeof(Exception).FullNameInCode()})");
        writer.Write($"await {_envelopeTransaction.Usage}.RollbackAsync();");
        writer.Write("throw;");
        writer.FinishBlock();
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _dbContext = chain.FindVariable(_dbContextType);
        yield return _dbContext;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }
}