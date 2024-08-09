using System.Data.Common;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.RDBMS.Sagas;

public class LoadSagaOperation : AsyncFrame
{
    private readonly Type _sagaType;
    private readonly Variable _sagaId;
    private Variable _storage;
    private Variable _cancellation;
    private Variable _transaction;

    public LoadSagaOperation(Type sagaType, Variable sagaId)
    {
        _sagaType = sagaType;
        _sagaId = sagaId;
        
        uses.Add(sagaId);
        
        Saga = new Variable(sagaType, this);
    }

    public Variable Saga { get; }

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
        writer.WriteLine($"var {Saga.Usage} = await {_storage.Usage}.LoadAsync<{Saga.VariableType.FullNameInCode()}, {_sagaId.VariableType.FullNameInCode()}>({_sagaId.Usage}, {_transaction.Usage}, {_cancellation.Usage});");
        Next?.GenerateCode(method, writer);
    }
}