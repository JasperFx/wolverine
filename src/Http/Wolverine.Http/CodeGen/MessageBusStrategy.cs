using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Lamar;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Http.CodeGen;

internal class MessageBusStrategy : IParameterStrategy
{
    public bool TryMatch(EndpointChain chain, IContainer container, ParameterInfo parameter, out Variable variable)
    {
        if (parameter.ParameterType == typeof(IMessageBus))
        {
            var context = new MessageContextFrame().Variable;
            variable = new Variable(typeof(IMessageBus), $"(({typeof(IMessageBus).FullNameInCode()}){context.Usage})",
                context.Creator);

            return true;
        }

        variable = null;
        return false;
    }
}