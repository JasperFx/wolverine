namespace Wolverine.Tracking;

#region sample_itrackedsession
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
    /// Records of all message activity that were scheduled for later sending or later local execution.
    /// Note that this collection can span records in the other collections
    /// </summary>
    RecordCollection Scheduled { get; }

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
    ///    Records of <see cref="Fault{T}"/> events auto-published by the failure pipeline
    ///    when handlers permanently fail. Identified by the <see cref="FaultHeaders.AutoPublished"/>
    ///    header on the outgoing envelope.
    /// </summary>
    RecordCollection AutoFaultsPublished { get; }

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
    /// Message processing records for messages that were discarded by exception handling policies
    /// in the application during the test
    /// </summary>
    RecordCollection Discarded { get; }

    /// <summary>
    ///     GH-4931. Every event-store session that committed appended events during the tracked session, in
    ///     commit order, each with the message being handled when it committed. Recorded by the Marten,
    ///     Polecat and Fisher integrations for sessions opened through Wolverine's outbox.
    /// </summary>
    IReadOnlyList<AppendedEvents> AppendedEvents => Array.Empty<AppendedEvents>();

    /// <summary>
    ///     GH-4940. Every Dynamic Consistency Boundary a handler or HTTP endpoint loaded during the tracked
    ///     session, in load order, each with the <see cref="JasperFx.Events.Tags.EventTagQuery" /> it was loaded
    ///     over and the message being handled. Recorded by Wolverine's generated code for <c>[DcbModel]</c> and
    ///     <c>[BoundaryModel]</c> parameters, on Marten, Polecat and Fisher alike.
    /// </summary>
    IReadOnlyList<LoadedBoundary> LoadedBoundaries => Array.Empty<LoadedBoundary>();

    /// <summary>
    ///     GH-4940. Every concurrent append <see cref="Tracking.ConcurrentAppends" /> landed during the tracked session:
    ///     an append armed under an act's correlation id, run on the first save of a session carrying it.
    /// </summary>
    IReadOnlyList<ConcurrentAppendRecord> ConcurrentAppends => Array.Empty<ConcurrentAppendRecord>();

    /// <summary>
    ///     GH-4939. The correlation id the tracked work ran under, when one was given through
    ///     <see cref="TrackedSessionConfiguration.WithCorrelationId" />. Null otherwise.
    /// </summary>
    string? CorrelationId => null;

    /// <summary>
    ///     Finds a message of type T that was either sent, received,
    ///     or executed during this session. This will throw an exception
    ///     if there is more than one message. Messages are counted by envelope,
    ///     so publishing two equal messages counts as two, while one message
    ///     routed to several destinations counts as one
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    T FindSingleTrackedMessageOfType<T>();

    /// <summary>
    ///     Find the single tracked message of type T for the given EventType. Will throw an exception
    ///     if there were more than one instance of this type. Messages are counted by envelope,
    ///     so publishing two equal messages counts as two, while one message
    ///     routed to several destinations counts as one
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

    /// <summary>
    /// Attempt to "play" any messages that were scheduled by *only* the initial IHost
    /// </summary>
    /// <remarks>
    /// GH-5700. A message for a local queue is delivered to that queue as if its time had come, so the endpoint's
    /// retries, dead-lettering and a saga's <c>Compensate</c> methods apply, and the returned session waits for all of
    /// it. A message for any other endpoint is sent there. Before 7.0, a local message ran inline and its
    /// exception was thrown at the test: pass <c>playInline: true</c> to keep that.
    /// </remarks>
    /// <param name="timeout"></param>
    /// <returns></returns>
    Task<ITrackedSession> PlayScheduledMessagesAsync(TimeSpan timeout);

    /// <inheritdoc cref="PlayScheduledMessagesAsync(TimeSpan)" />
    /// <param name="timeout">How long the returned session waits for what the replay caused.</param>
    /// <param name="playInline">
    /// Run a message for a local queue inline, through <c>InvokeAsync()</c>, as Wolverine did before 7.0: only inline
    /// retries run, there is no dead-lettering or compensation, and the failure is thrown at the test
    /// </param>
    Task<ITrackedSession> PlayScheduledMessagesAsync(TimeSpan timeout, bool playInline)
        => playInline
            ? throw new NotSupportedException($"{GetType().FullName} cannot play scheduled messages inline")
            : PlayScheduledMessagesAsync(timeout);

    /// <summary>
    /// GH-5664. Let <paramref name="advanceBy" /> pass on the session's <see cref="Clock" />, and play only the messages
    /// scheduled by the initial IHost that come due within that window, in due order. That includes a message scheduled
    /// while the window plays, such as a saga's next poll, when it is due before the window ends. Everything due later
    /// stays scheduled, and calling this again on the session it returns continues from where this one stopped.
    /// </summary>
    /// <remarks>
    /// The clock is the application's own <see cref="TimeProvider" /> when the host registers one the session can move
    /// (a public <c>Advance(TimeSpan)</c> method, as on <c>FakeTimeProvider</c>), or the clock given to
    /// <see cref="TrackedSessionConfiguration.UseClock" />, so a saga reads the due time of the message it is handling.
    /// Otherwise the session keeps a clock of its own, which the application does not read. Testing only: nothing in
    /// the runtime reads the session's clock.
    /// <para>
    /// GH-5700. A message for a local queue is delivered to that queue as if its time had come, so the endpoint's
    /// retries, dead-lettering and a saga's <c>Compensate</c> methods apply, and the window waits for all of it before
    /// it plays the next due message. A failure is not thrown at the test. Pass <c>playInline: true</c> to run local
    /// messages inline instead.
    /// </para>
    /// </remarks>
    /// <param name="advanceBy">How much time passes. Call it by name, <c>PlayScheduledMessagesAsync(advanceBy: 1.Hours())</c>, since a single positional TimeSpan is the timeout of the overload that plays everything.</param>
    /// <param name="timeout">How long the returned session waits for what the window caused. This session's timeout by default.</param>
    /// <param name="playInline">
    /// Run a message for a local queue inline, through <c>InvokeAsync()</c>: only inline retries run, there is no
    /// dead-lettering or compensation, and the failure is thrown at the test
    /// </param>
    /// <returns>A new session holding what the window played and caused</returns>
    Task<ITrackedSession> PlayScheduledMessagesAsync(TimeSpan advanceBy, TimeSpan? timeout = null, bool playInline = false)
        => throw new NotSupportedException($"{GetType().FullName} cannot advance time");

    /// <summary>
    /// GH-5664. The session's virtual "now", which <see cref="PlayScheduledMessagesAsync(TimeSpan, TimeSpan?, bool)" /> moves
    /// forward. It is carried to the session that call returns.
    /// </summary>
    TimeProvider Clock => TimeProvider.System;
}

#endregion