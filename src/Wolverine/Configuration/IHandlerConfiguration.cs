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


    /// <summary>
    ///     Applies a handler policy to all known message handlers
    /// </summary>
    /// <typeparam name="T"></typeparam>
    void GlobalPolicy<T>() where T : IHandlerPolicy, new();

    /// <summary>
    ///     Applies a handler policy to all known message handlers
    /// </summary>
    /// <param name="policy"></param>
    void GlobalPolicy(IHandlerPolicy policy);

    /// <summary>
    ///     Make configurations to the message handling for one
    ///     specific message type T
    /// </summary>
    /// <param name="configure"></param>
    /// <typeparam name="T"></typeparam>
    void ConfigureHandlerForMessage<T>(Action<HandlerChain> configure);


    /// <summary>
    ///     Make configurations to the message handling for one
    ///     specific message type specified by messageType
    /// </summary>
    /// <param name="messageType"></param>
    /// <param name="configure"></param>
    void ConfigureHandlerForMessage(Type messageType, Action<HandlerChain> configure);
}
