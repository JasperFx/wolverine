using Wolverine.Runtime.RemoteInvocation;

namespace Wolverine;

/// <summary>
///     Send or invoke messages to a specific endpoint
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
    ///     Execute the message at this destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    Task<Acknowledgement> InvokeAsync(object message, CancellationToken cancellation = default,
        TimeSpan? timeout = null);

    /// <summary>
    ///     Execute the message at this destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options">Use to pass in extra metadata like headers or group id or correlation information to the command execution</param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <returns></returns>
    Task<Acknowledgement> InvokeAsync(object message, DeliveryOptions options, CancellationToken cancellation = default,
        TimeSpan? timeout = null);

    /// <summary>
    ///     Execute the summary at this destination and retrieve the expected
    ///     response from the destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T> InvokeAsync<T>(object message, CancellationToken cancellation = default, TimeSpan? timeout = null)
        where T : class;

    /// <summary>
    ///     Execute the summary at this destination and retrieve the expected
    ///     response from the destination
    /// </summary>
    /// <param name="message"></param>
    /// <param name="options">Use to pass in extra metadata like headers or group id or correlation information to the command execution</param>
    /// <param name="cancellation"></param>
    /// <param name="timeout"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Task<T> InvokeAsync<T>(object message, DeliveryOptions options, CancellationToken cancellation = default, TimeSpan? timeout = null)
        where T : class;

    /// <summary>
    /// Send a message by its raw binary contents and optionally configure how Wolverine
    /// will send this
    /// </summary>
    /// <param name="data"></param>
    /// <param name="messageType">The .NET type for this message if known. If supplied, this will help Wolverine apply any configured sending policies for this message type</param>
    /// <param name="configure"></param>
    /// <returns></returns>
    ValueTask SendRawMessageAsync(byte[] data, Type? messageType = null, Action<Envelope>? configure = null);
}