using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class MessageBusSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IMessageBus) || type == typeof(IMessageContext) || type == typeof(MessageContext);
    }

    public Variable Create(Type type)
    {
        return new CreateMessageContextWithMaybeTenantFrame().Variable;
    }
}

internal class CreateMessageContextWithMaybeTenantFrame : SyncFrame
{
    private Variable? _tenantId;
    private Variable? _runtime;
    public CastVariable IMessageContextVariable { get; }
    public CastVariable IMessageBusVariable { get; }

    public CreateMessageContextWithMaybeTenantFrame()
    {
        Variable = new Variable(typeof(MessageContext), this);
        IMessageContextVariable = new CastVariable(Variable, typeof(IMessageContext));
        creates.Add(IMessageContextVariable);
        IMessageBusVariable = new CastVariable(Variable, typeof(IMessageBus));
        creates.Add(IMessageBusVariable);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"var {Variable.Usage} = new {typeof(MessageContext).FullNameInCode()}({_runtime!.Usage});");
        if (_tenantId != null)
        {
            writer.Write($"{Variable.Usage}.{nameof(IMessageBus.TenantId)} = {_tenantId.Usage};");
        }

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _runtime = chain.FindVariable(typeof(IWolverineRuntime));
        yield return _runtime;

        if (chain.TryFindVariableByName(typeof(string), PersistenceConstants.TenantIdVariableName, out _tenantId))
        {
            yield return _tenantId;
        }
    }
}