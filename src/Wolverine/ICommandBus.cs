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
    /// Schedule the publishing or execution of a message until a later time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="time"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    ValueTask ScheduleAsync<T>(T message, DateTimeOffset time, DeliveryOptions? options = null);

    /// <summary>
    /// Schedule the publishing or execution of a message until a later time
    /// </summary>
    /// <param name="message"></param>
    /// <param name="delay"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    ValueTask ScheduleAsync<T>(T message, TimeSpan delay, DeliveryOptions? options = null);

}