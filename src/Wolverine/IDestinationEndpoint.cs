using System;
using System.Threading;
using System.Threading.Tasks;
using Wolverine.Runtime.ResponseReply;

namespace Wolverine;

/// <summary>
/// Send or invoke messages to a specific endpoint
/// </summary>
public interface IDestinationEndpoint
{
    Uri Uri { get; }
    string EndpointName { get; }
    
    /// <summary>
    ///     Sends a message to this destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    ValueTask SendAsync<T>(T message, DeliveryOptions? options = null);
    
    /// <summary>
    /// Execute the message at this destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null);
    
    /// <summary>
    /// Execute the summary at this destination and retrieve the expected
    /// response from the destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        where T : class;
}