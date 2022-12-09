using System;
using System.Threading;
using System.Threading.Tasks;

namespace Wolverine;

/// <summary>
///     Slimmed down version of IMessageContext strictly for local command execution
/// </summary>
public interface ICommandBus
{
    /// <summary>
    /// Execute the message handling for this message *right now* and wait for the completion.
    /// If the message is handled locally, this delegates immediately
    /// If the message is handled remotely, the message is sent and the method waits for the response
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout">Optional timeout</param>
    /// <returns></returns>
    Task InvokeAsync(object message, CancellationToken cancellation = default, TimeSpan? timeout = default);


    /// <summary>
    /// Execute the message handling for this message *right now* and wait for the completion and the designated response type T.
    /// If the message is handled locally, this delegates immediately
    /// If the message is handled remotely, the message is sent and the method waits for the response
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout">Optional timeout</param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T?> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = default);

    /// <summary>
    ///     Schedule a message to be processed in this application at a specified time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="executionTime"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<Guid> ScheduleAsync<T>(T message, DateTimeOffset executionTime);

    /// <summary>
    ///     Schedule a message to be processed in this application at a specified time with a delay
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<Guid> ScheduleAsync<T>(T message, TimeSpan delay);
}