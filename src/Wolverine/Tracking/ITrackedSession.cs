using System;
using System.Collections.Generic;

namespace Wolverine.Tracking;

#region sample_ITrackedSession

public interface ITrackedSession
{
    /// <summary>
    ///     Completion status of the current messaging session
    /// </summary>
    TrackingStatus Status { get; }

    /// <summary>
    ///     Finds a message of type T that was either sent, received,
    ///     or executed during this session. This will throw an exception
    ///     if there is more than one message
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T FindSingleTrackedMessageOfType<T>();

    /// <summary>
    ///     Return an array of the unique messages sent, received, or handled
    /// </summary>
    /// <returns></returns>
    IEnumerable<object> UniqueMessages();

    /// <summary>
    ///     Return an array of the unique messages sent, received, or handled
    ///     for a particular EventType
    /// </summary>
    /// <returns></returns>
    IEnumerable<object> UniqueMessages(EventType eventType);

    /// <summary>
    ///     Find the single tracked message of type T for the given EventType. Will throw an exception
    ///     if there were more than one instance of this type
    /// </summary>
    /// <param name="eventType"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T FindSingleTrackedMessageOfType<T>(EventType eventType);

    /// <summary>
    ///     Finds the processing history for any messages of type T and
    ///     the given EventType
    /// </summary>
    /// <param name="eventType"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    EnvelopeRecord[] FindEnvelopesWithMessageType<T>(EventType eventType);

    /// <summary>
    ///     Find all envelope records where the message type is T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    EnvelopeRecord[] FindEnvelopesWithMessageType<T>();

    /// <summary>
    ///     Access all the activity in the time order they
    ///     were logged
    /// </summary>
    /// <returns></returns>
    EnvelopeRecord[] AllRecordsInOrder();

    /// <summary>
    ///     Was there zero activity tracked
    /// </summary>
    /// <returns></returns>
    bool HasNoRecordsOfAnyKind();

    /// <summary>
    ///     Access all the activity in the time order they
    ///     were logged for the given EventType
    /// </summary>
    /// <returns></returns>
    EnvelopeRecord[] AllRecordsInOrder(EventType eventType);

    /// <summary>
    ///     All exceptions thrown during the lifetime of this
    ///     tracked session
    /// </summary>
    /// <returns></returns>
    IReadOnlyList<Exception> AllExceptions();

    /// <summary>
    /// Find the single, expected envelope received
    /// for the message type "T"
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Envelope FindSingleReceivedEnvelopeForMessageType<T>();

    /// <summary>
    /// Find the single, expected envelope that was *executed*
    /// for the message type "T"
    ///
    /// Use this for messages that were processed through
    /// ICommandBus.InvokeAsync()
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    Envelope FindSingleExecutedEnvelopeForMessageType<T>();
}

#endregion
