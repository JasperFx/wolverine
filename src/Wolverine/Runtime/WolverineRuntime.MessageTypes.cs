using System;
using System.Linq;
using JasperFx.Core;
using Wolverine.Util;

namespace Wolverine.Runtime;

public partial class WolverineRuntime
{
    public void RegisterMessageType(Type messageType)
    {
        Handlers.RegisterMessageType(messageType);
    }

}