using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;

namespace Wolverine.Persistence.Sagas;

public class LoadSagaOperation : AsyncFrame
{
    private readonly Type _sagaType;
    private readonly Variable _sagaId;
    private Variable _storage;
    private Variable _cancellation;

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
        _storage = chain.FindVariable(typeof(ISagaStorage<,>).MakeGenericType(_sagaId.VariableType, _sagaType));
        yield return _storage;
        
        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteLine($"var {Saga.Usage} = await {_storage.Usage}.LoadAsync({_sagaId.Usage}, {_cancellation.Usage});");
        Next?.GenerateCode(method, writer);
    }
}