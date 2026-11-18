using Wolverine.ErrorHandling;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Internal policy holder for auto-publishing of <see cref="Fault{T}"/> events.
    /// Configured via <see cref="PublishFaultEvents"/> and per-type overrides on
    /// <see cref="MessageTypePolicies{T}"/>.
    /// </summary>
    internal FaultPublishingPolicy FaultPublishing { get; } = new();

    /// <summary>
    /// Globally enable auto-publishing of <see cref="Fault{T}"/> events whenever
    /// a handler permanently fails for a message that has been moved to the
    /// dead-letter queue. Per-message-type opt-out is available via
    /// <see cref="MessageTypePolicies{T}.DoNotPublishFault"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Delivery semantics.</b> Since 7.0 (GH-5554), a fault for a destination with a durable sending
    /// endpoint is written in the same transaction as the dead-letter move, when the message came in on a
    /// durable endpoint and the message store can do that (PostgreSQL, SQL Server, MySQL, SQLite, RavenDB).
    /// Every other fault is sent right after the move, best-effort: inline and buffered endpoints, dead
    /// letters kept by the broker, non-durable destinations, and the Oracle and CosmosDB stores. A
    /// best-effort fault is lost if the process crashes between the dead-letter move and the send.
    /// </para>
    /// <para>
    /// <b>Scope.</b> Fault events are emitted only on the receiving end, when a handler permanently
    /// fails and the envelope is moved to the dead-letter queue (or discarded, with
    /// <c>includeDiscarded: true</c>). Three paths bypass this and never produce a fault event:
    /// send-side dead-letter movements (when an outgoing envelope can't be delivered after retries);
    /// envelopes whose message-type name doesn't resolve to a known handler (no <c>T</c> to construct
    /// a <see cref="Fault{T}"/> for); and envelopes that arrive expired (their <c>DeliverBy</c> has
    /// elapsed and they are discarded before deserialization).
    /// </para>
    /// <para>
    /// <b>Exception redaction.</b> By default, <see cref="Fault{T}.Exception"/> carries the full
    /// exception message and stack trace, recursed through inner exceptions. A common throw
    /// pattern like <c>throw new ValidationException($"Card {model.CardNumber} declined")</c>
    /// places payload-derived plaintext in the captured message. In regulated environments,
    /// pass <c>includeExceptionMessage: false</c> to set every captured
    /// <see cref="ExceptionInfo.Message"/> to <see cref="string.Empty"/>, and/or
    /// <c>includeStackTrace: false</c> to drop every captured <see cref="ExceptionInfo.StackTrace"/>.
    /// The <see cref="ExceptionInfo.Type"/> field is always preserved. Both flags apply
    /// recursively to inner exceptions and to <see cref="AggregateException.InnerExceptions"/>.
    /// Per-type overrides via <see cref="MessageTypePolicies{T}.PublishFault"/> set their own
    /// redaction values explicitly and do not inherit subsequent changes to these globals.
    /// If <c>T</c> itself is sensitive, pair this with <see cref="MessageTypePolicies{T}.Encrypt"/>
    /// so the entire <see cref="Fault{T}"/> body travels encrypted on the wire; the encryption
    /// guide's "Fault events" section covers the full interaction model.
    /// </para>
    /// <para>
    /// <b>Pre-handler failures.</b> Envelopes that fail before handler dispatch produce no
    /// <see cref="Fault{T}"/> because the message instance is not available to wrap — including
    /// <see cref="Wolverine.Runtime.Serialization.Encryption.MessageDecryptionException"/>,
    /// <see cref="Wolverine.Runtime.Serialization.Encryption.EncryptionKeyNotFoundException"/>,
    /// and <see cref="Wolverine.Runtime.Serialization.Encryption.EncryptionPolicyViolationException"/>.
    /// The original envelope still routes to the dead-letter queue.
    /// </para>
    /// </remarks>
    public WolverineOptions PublishFaultEvents(
        bool includeDiscarded = false,
        bool includeExceptionMessage = true,
        bool includeStackTrace = true)
    {
        FaultPublishing.GlobalMode = includeDiscarded
            ? FaultPublishingMode.DlqAndDiscard
            : FaultPublishingMode.DlqOnly;
        FaultPublishing.GlobalIncludeExceptionMessage = includeExceptionMessage;
        FaultPublishing.GlobalIncludeStackTrace = includeStackTrace;
        return this;
    }
}
