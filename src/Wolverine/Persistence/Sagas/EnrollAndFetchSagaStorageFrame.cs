using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;

namespace Wolverine.Persistence.Sagas;

public class EnrollAndFetchSagaStorageFrame<TId, TSaga> : AsyncFrame, ISagaStorageFrame where TSaga : Saga
{
    private Variable _context;
    private Variable _cancellation;

    public EnrollAndFetchSagaStorageFrame()
    {
        Variable = new Variable(typeof(ISagaStorage<TId, TSaga>), this);
        SimpleVariable = new Variable(typeof(ISagaStorage<TSaga>), Variable.Usage + "_Slim", this);
    }

    public Variable SimpleVariable { get; }

    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"await using var {Variable.Usage} = await {typeof(SagaSupport<TId, TSaga>).FullNameInCode()}.{nameof(SagaSupport<TId, TSaga>.EnrollAndFetchSagaStorage)}({_context.Usage});");
        writer.Write($"var {SimpleVariable.Usage} = {Variable.Usage};");
        
        Next?.GenerateCode(method, writer);
        
        writer.Write($"await {Variable.Usage}.{nameof(ISagaStorage<TId, TSaga>.SaveChangesAsync)}({_cancellation.Usage});");
    }
}