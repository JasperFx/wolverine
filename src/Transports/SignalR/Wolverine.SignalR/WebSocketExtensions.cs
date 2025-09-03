using Wolverine.SignalR.Internals;

namespace Wolverine.SignalR;

public static class WebSocketExtensions
{
    /// <summary>
    /// Send this message back to the SignalR connection where the current message
    /// was received from
    /// </summary>
    /// <param name="Message"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static ResponseToCaller<T> RespondToCaller<T>(this T Message) => new(Message);

    /// <summary>
    /// Send this message to the specified group of connections on the SignalR hub
    /// where the current message was received
    /// </summary>
    /// <param name="Message"></param>
    /// <param name="GroupName"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    public static SignalRMessage<T> SendToGroup<T>(this T Message, string GroupName) =>
        new(Message, new WebSocketRouting.Group(GroupName));
}

// TODO -- response to user?