using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Runtime;

namespace Wolverine.Http.CodeGen;

internal class MessageBusStrategy : IParameterStrategy
{
    public bool TryMatch(HttpChain chain, IServiceContainer container, ParameterInfo parameter, out Variable? variable)
    {
        if (parameter.ParameterType == typeof(IMessageBus))
        {
            variable = new UseMessageBusFrame().Bus;

            return true;
        }

        variable = default!;
        return false;
    }
}

internal class UseMessageBusFrame : SyncFrame
{
    public UseMessageBusFrame()
    {
        Bus = new Variable(typeof(IMessageBus), "messageContext", this);
    }

    public Variable Bus { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return chain.FindVariable(typeof(MessageContext));
    }
}