using System.Data.Common;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.RDBMS.Sagas;

public class SagaOperation : AsyncFrame
{
    private readonly Variable _saga;
    private readonly SagaOperationType _operation;
    private Variable _storage;
    private Variable _cancellation;
    private Variable _transaction;

    public SagaOperation(Variable saga, SagaOperationType operation)
    {
        _saga = saga;
        _operation = operation;
        uses.Add(saga);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _storage = chain.FindVariable(typeof(IDatabaseSagaStorage));
        yield return _storage;
        
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
        
        _transaction = chain.FindVariable(typeof(DbTransaction));
        yield return _transaction;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"await {_storage.Usage}.{_operation}({_saga.Usage}, {_transaction.Usage}, {_cancellation.Usage});");
        Next?.GenerateCode(method, writer);
    }
}