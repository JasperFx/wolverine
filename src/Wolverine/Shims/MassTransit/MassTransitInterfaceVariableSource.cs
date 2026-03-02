using JasperFx.CodeGeneration.Model;

namespace Wolverine.Shims.MassTransit;

/// <summary>
/// Code generation variable source that provides <see cref="IPublishEndpoint"/> and
/// <see cref="ISendEndpointProvider"/> as cast variables from the current message context.
/// Since <see cref="Wolverine.Runtime.MessageBus"/> directly implements both interfaces,
/// the generated code simply casts the existing context variable.
/// </summary>
internal class MassTransitInterfaceVariableSource : IVariableSource
{
    public bool Matches(Type type)
    {
        return type == typeof(IPublishEndpoint) || type == typeof(ISendEndpointProvider);
    }

    public Variable Create(Type type)
    {
        // The "context" variable is a MessageContext (which extends MessageBus),
        // and MessageBus implements IPublishEndpoint and ISendEndpointProvider.
        // Create a cast from the existing context variable.
        var contextVariable = new Variable(typeof(IMessageContext), "context");
        return new CastVariable(contextVariable, type);
    }
}
