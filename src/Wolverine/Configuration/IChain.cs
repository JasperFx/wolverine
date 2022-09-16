using System;
using System.Collections.Generic;
using Lamar;
using LamarCodeGeneration.Frames;

namespace Wolverine.Configuration;

#region sample_IChain

/// <summary>
///     Models the middleware arrangement for either an HTTP route execution
///     or the execution of a message
/// </summary>
public interface IChain
{
    /// <summary>
    ///     Frames that would be initially placed in front of
    ///     the primary action(s)
    /// </summary>
    IList<Frame> Middleware { get; }

    /// <summary>
    ///     Frames that would be initially placed behind the primary
    ///     action(s)
    /// </summary>
    IList<Frame> Postprocessors { get; }

    /// <summary>
    ///     A description of this frame
    /// </summary>
    string Description { get; }

    /// <summary>
    ///     Used internally by Wolverine for "outbox" mechanics
    /// </summary>
    /// <returns></returns>
    bool ShouldFlushOutgoingMessages();

    MethodCall[] HandlerCalls();

    /// <summary>
    ///     Find all of the service dependencies of the current chain
    /// </summary>
    /// <param name="chain"></param>
    /// <param name="container"></param>
    /// <returns></returns>
    IEnumerable<Type> ServiceDependencies(IContainer container);
}

#endregion
