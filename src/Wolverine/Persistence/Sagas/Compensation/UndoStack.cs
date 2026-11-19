using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using JasperFx.Core.Reflection;
using Wolverine.Util;

// ReSharper disable once CheckNamespace
namespace Wolverine
{
    /// <summary>
    /// GH-5673. One entry of a <see cref="CompensatingSaga" />'s undo stack: an undo message, kept serialized with the
    /// saga's state, and the step that pushed it.
    /// </summary>
    public sealed class UndoEntry
    {
        public Guid Id { get; set; }

        /// <summary>The optional label the saga pushed it under, such as <c>"flight"</c></summary>
        public string? Step { get; set; }

        /// <summary>Wolverine's message type name of the message the saga was handling when it pushed this entry</summary>
        public string PushedBy { get; set; } = "";

        /// <summary>Wolverine's message type name of the undo message</summary>
        public string MessageType { get; set; } = "";

        /// <summary>The undo message's .NET type, to read <see cref="Body" /> back</summary>
        public string DotNetType { get; set; } = "";

        /// <summary>The undo message, as JSON</summary>
        public string Body { get; set; } = "";

        /// <summary>
        /// Pushed before its step, and the step has not been confirmed yet: by the step's reply, or by
        /// <c>ConfirmUndo(step)</c>. An unwind includes pending entries unless it is told not to
        /// </summary>
        public bool Pending { get; set; }

        /// <summary>
        /// The .NET type of a business reply that confirms this undo in a sequential unwind, instead of Wolverine's
        /// acknowledgement
        /// </summary>
        public string? ConfirmedBy { get; set; }

        /// <summary>Sent in a sequential unwind, and waiting for its confirmation</summary>
        public bool Undoing { get; set; }

        /// <summary>How the entry reads in the compensation log: its step label, else the message that pushed it</summary>
        public string Tag => Step ?? PushedBy;

        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.General);

        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Undo messages are application message types, rooted by the saga that pushes them.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Undo messages are application message types, rooted by the saga that pushes them.")]
        internal static UndoEntry For(object message, string? step, Type? confirmedBy)
        {
            ArgumentNullException.ThrowIfNull(message);
            var type = message.GetType();
            return new UndoEntry
            {
                Id = Guid.NewGuid(),
                Step = step,
                MessageType = type.ToMessageTypeName(),
                DotNetType = $"{type.FullName}, {type.Assembly.GetName().Name}",
                Body = JsonSerializer.Serialize(message, type, Json),
                Pending = true,
                ConfirmedBy = confirmedBy == null ? null : $"{confirmedBy.FullName}, {confirmedBy.Assembly.GetName().Name}"
            };
        }

        /// <summary>The undo message, read back from <see cref="Body" /></summary>
        [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Undo messages are application message types, rooted by the saga that pushes them.")]
        [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "Undo messages are application message types, rooted by the saga that pushes them.")]
        [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Undo messages are application message types, rooted by the saga that pushes them.")]
        public object ToMessage()
        {
            var type = Type.GetType(DotNetType, true)!;
            return JsonSerializer.Deserialize(Body, type, Json)!;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2057", Justification = "The reply type the saga named when it pushed the entry.")]
        internal Type? ConfirmedByType() => ConfirmedBy == null ? null : Type.GetType(ConfirmedBy, true);

        internal UndoEntry Clone() => (UndoEntry)MemberwiseClone();

        internal UndoneStep ToUndone() => new(Tag, MessageType);
    }

    /// <summary>GH-5673. An undo message a compensation sent: its step tag and its message type name.</summary>
    public sealed record UndoneStep(string Step, string MessageType);

    /// <summary>
    /// GH-5673. A sequential unwind in progress: the failure it answers, and what it has undone so far.
    /// </summary>
    public sealed class SagaUnwind
    {
        /// <summary>Wolverine's message type name of the failed message</summary>
        public string Step { get; set; } = "";

        /// <summary>The full name of the exception the failed message threw</summary>
        public string ExceptionType { get; set; } = "";

        /// <summary>The envelope id of the failed message, when known</summary>
        public Guid? FailedMessageId { get; set; }

        /// <summary>Does the unwind undo entries whose step was never confirmed too?</summary>
        public bool IncludePending { get; set; } = true;

        /// <summary>The undo messages confirmed so far, oldest first</summary>
        public List<UndoneStep> Undone { get; set; } = [];
    }

    /// <summary>
    /// GH-5673. What <c>UndoSequentially()</c> returns: the first undo message of the unwind, sent asking to be confirmed.
    /// Return it from a <c>Compensate</c> method.
    /// </summary>
    public sealed class UndoSequence : OutgoingMessages
    {
        internal UndoSequence(bool includePending)
        {
            IncludePending = includePending;
        }

        internal bool IncludePending { get; }

        /// <summary>The stack had nothing to undo, so the unwind is already over</summary>
        public bool IsEmpty => Count == 0;
    }

    // ---- Wolverine's own events for an event-sourced saga's undo stack ---------------------------------------------

    /// <summary>GH-5673. Wolverine's own event: the saga pushed <paramref name="Entry" /> onto its undo stack.</summary>
    public sealed record SagaUndoPushed(UndoEntry Entry);

    /// <summary>GH-5673. Wolverine's own event: the step behind a pending undo entry was confirmed.</summary>
    public sealed record SagaUndoConfirmed(Guid EntryId);

    /// <summary>
    /// GH-5673. Wolverine's own event: an undo message was sent. In a sequential unwind it waits on the stack for its
    /// confirmation (<paramref name="AwaitsConfirmation" />); otherwise it leaves the stack.
    /// </summary>
    public sealed record SagaUndoSent(Guid EntryId, bool AwaitsConfirmation);

    /// <summary>GH-5673. Wolverine's own event: an undo message of a sequential unwind was confirmed.</summary>
    public sealed record SagaUndoCompleted(Guid EntryId);

    /// <summary>
    /// GH-5673. Wolverine's own event: an undo message of a sequential unwind failed for good. The entry stays on the
    /// stack, so the unwind can be retried from there.
    /// </summary>
    public sealed record SagaUndoFailed(Guid EntryId, string Reason);
}

namespace Wolverine.Persistence.Sagas
{
    /// <summary>
    /// GH-5673. How a sequential unwind hears back about one undo message: Wolverine's acknowledgement, the business reply
    /// the saga named, or the undo message's fault. Wolverine turns the reply into this message for the saga's own
    /// chain. Not for application code.
    /// </summary>
    public abstract record UndoReply(Guid UndoEntryId, bool Succeeded, string? Failure);

    /// <inheritdoc cref="UndoReply" />
    public sealed record UndoReply<TSaga>(Guid UndoEntryId, bool Succeeded, string? Failure)
        : UndoReply(UndoEntryId, Succeeded, Failure) where TSaga : CompensatingSaga;
}
