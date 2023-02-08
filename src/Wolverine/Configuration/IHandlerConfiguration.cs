using System;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Configuration;

public interface IHandlerConfiguration : IWithFailurePolicies
{
    /// <summary>
    ///     Configure how Wolverine discovers message handler classes to override
    ///     the built in conventions
    /// </summary>
    /// <param name="configure"></param>
    /// <returns></returns>
    IHandlerConfiguration Discovery(Action<HandlerSource> configure);
    
}