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
    void AddPolicy<T>() where T : IHandlerPolicy, new();

    /// <summary>
    ///     Applies a handler policy to all known message handlers
    /// </summary>
    /// <param name="policy"></param>
    void AddPolicy(IHandlerPolicy policy);

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    /// <typeparam name="T">The actual middleware type</typeparam>
    void AddMiddleware<T>(Func<HandlerChain, bool>? filter = null);

    /// <summary>
    ///     Add Wolverine middleware to message handlers
    /// </summary>
    /// <param name="middlewareType">The actual middleware type</param>
    /// <param name="filter">If specified, limits the applicability of the middleware to certain message types</param>
    void AddMiddleware(Type middlewareType, Func<HandlerChain, bool>? filter = null);

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

    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    void AddMiddlewareByMessageType(Type middlewareType);
}