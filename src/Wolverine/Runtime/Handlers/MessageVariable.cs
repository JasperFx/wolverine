using System;
using LamarCodeGeneration.Model;

namespace Wolverine.Runtime.Handlers;

internal class MessageVariable : Variable
{
    public MessageVariable(Variable envelope, Type messageType) : base(messageType, DefaultArgName(messageType))
    {
        Creator = new MessageFrame(this, envelope);
    }
}
