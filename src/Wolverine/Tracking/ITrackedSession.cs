namespace Wolverine.Tracking;

#region sample_ITrackedSession

public interface ITrackedSession
{
    /// <summary>
    ///     Completion status of the current messaging session
    /// </summary>
    TrackingStatus Status { get; }

    /// <summary>
    ///     Records of all messages received during the tracked session
    /// </summary>
    RecordCollection Received { get; }
    
    /// <summary>
    ///     Records of all messages sent during the tracked session. This will include messages
    ///     published to local queues
    /// </summary>
    RecordCollection Sent { get; }

    /// <summary>
    ///     Records of all messages that were executed during the tracked session
    /// </summary>
    RecordCollection ExecutionStarted { get; }

    /// <summary>
    ///     Records of all messages that were successfully executed during the tracked session
    /// </summary>
    RecordCollection ExecutionFinished { get; }
    
    /// <summary>
    ///   Records of all messages that successfully completed their processing
    /// </summary>
    RecordCollection MessageSucceeded { get; }
    
    /// <summary>
    ///     Records of all messages that failed during processing
    /// </summary>
    RecordCollection MessageFailed { get; }

    /// <summary>
    ///    Records of all messages which have no handlers
    /// </summary>
    RecordCollection NoHandlers { get; }
    
    /// <summary>
    ///    Records of all messages received during the tracked session that were not routed
    /// </summary>
    RecordCollection NoRoutes { get; }

    /// <summary>
    ///    Records of all messages that were moved to the error queue
    /// </summary>
    RecordCollection MovedToErrorQueue { get; }
    
    /// <summary>
    ///    Records of all messages that were moved to the error queue
    /// </summary>
    RecordCollection MovedToRetryQueue { get; }
    
    /// <summary>
    ///     Records of all messages that were requeued
    /// </summary>
    RecordCollection Requeued { get; }
    
    /// <summary>
    ///     Message processing records for messages that were executed. Note that this includes message
    ///     executions that failed and additional attempts as a separate record in the case of retries
    /// </summary>
    RecordCollection Executed { get; }

    /// <summary>
    ///     Finds a message of type T that was either sent, received,
    ///     or executed during this session. This will throw an exception
    ///     if there is more than one message
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T FindSingleTrackedMessageOfType<T>();

    /// <summary>
    ///     Find the single tracked message of type T for the given EventType. Will throw an exception
    ///     if there were more than one instance of this type
    /// </summary>
    /// <param name="eventType"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T FindSingleTrackedMessageOfType<T>(MessageEventType eventType);

    /// <summary>
    ///     Finds the processing history for any messages of type T and
    ///     the given EventType
    /// </summary>
    /// <param name="eventType"></param>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    EnvelopeRecord[] FindEnvelopesWithMessageType<T>(MessageEventType eventType);

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
    ///     Access all the activity in the time order they
    ///     were logged for the given EventType
    /// </summary>
    /// <returns></returns>
    EnvelopeRecord[] AllRecordsInOrder(MessageEventType eventType);

    /// <summary>
    ///     All exceptions thrown during the lifetime of this
    ///     tracked session
    /// </summary>
    /// <returns></returns>
    IReadOnlyList<Exception> AllExceptions();

    /// <summary>
    /// Will throw an assertion exception with the tracked message history if the supplied condition
    /// returns false
    /// </summary>
    /// <param name="message"></param>
    /// <param name="condition"></param>
    void AssertCondition(string message, Func<bool> condition);
}

#endregion