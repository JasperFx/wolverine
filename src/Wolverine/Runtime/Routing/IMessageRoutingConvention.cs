using System;
using System.Collections.Generic;
using Wolverine.Configuration;

namespace Wolverine.Runtime.Routing;

/// <summary>
/// Plugin for doing any kind of conventional message routing
/// </summary>
public interface IMessageRoutingConvention
{
    void DiscoverListeners(IWolverineRuntime runtime, IReadOnlyList<Type> handledMessageTypes);
    IEnumerable<Endpoint> DiscoverSenders(Type messageType, IWolverineRuntime runtime);
}
