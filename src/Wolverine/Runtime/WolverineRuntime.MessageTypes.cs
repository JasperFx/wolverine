using System;
using System.Linq;
using Baseline;
using Wolverine.Util;

namespace Wolverine.Runtime;

internal partial class WolverineRuntime
{
    public bool TryFindMessageType(string messageTypeName, out Type messageType)
    {
        return Handlers.TryFindMessageType(messageTypeName, out messageType);
    }

    public Type DetermineMessageType(Envelope envelope)
    {
        if (envelope.Message == null)
        {
            if (TryFindMessageType(envelope.MessageType!, out var messageType))
            {
                return messageType;
            }

            throw new InvalidOperationException(
                $"Unable to determine a message type for `{envelope.MessageType}`, the known types are: {Handlers.Chains.Select(x => x.MessageType.ToMessageTypeName()).Join(", ")}");
        }

        if (envelope.Message == null)
        {
            throw new ArgumentNullException(nameof(Envelope.Message));
        }

        return envelope.Message.GetType();
    }

    public void RegisterMessageType(Type messageType)
    {
        Handlers.RegisterMessageType(messageType);
    }
}
