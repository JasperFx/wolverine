using System.Data.Common;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Persistence;

namespace Wolverine.RDBMS.Sagas;

public class SagaOperation : AsyncFrame, ISagaOperation
{
    private Variable _storage;
    private Variable _cancellation;
    private Variable _transaction;

    public SagaOperation(Variable saga, SagaOperationType operation)
    {
        Saga = saga;
        Operation = operation;
        uses.Add(saga);
    }

    public Variable Saga { get; }

    public SagaOperationType Operation { get; }

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
        writer.WriteLine($"await {_storage.Usage}.{Operation}({Saga.Usage}, {_transaction.Usage}, {_cancellation.Usage});");
        Next?.GenerateCode(method, writer);
    }
}