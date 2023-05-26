using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Handlers;

public class MessageContextFrame : SyncFrame
{
    private Variable _runtime;

    public MessageContextFrame()
    {
        Variable = new Variable(typeof(MessageContext), this);
        
        creates.Add(new CastVariable(Variable, typeof(IMessageContext)));
        creates.Add(new CastVariable(Variable, typeof(IMessageBus)));
        
    }
    
    public Variable Variable { get; }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _runtime = chain.FindVariable(typeof(IWolverineRuntime));
        yield return _runtime;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = new {typeof(MessageContext).FullNameInCode()}({_runtime.Usage});");
        Next?.GenerateCode(method, writer);
    }
}
