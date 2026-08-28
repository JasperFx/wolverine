using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Codegen;

/// <summary>
/// GH-4180. The message-handler answer to a failed deduplication check: stop before the handler
/// runs, and let the message be acknowledged normally.
///
/// <para>
/// Returning — rather than throwing <c>DuplicateIncomingEnvelopeException</c> — is deliberate. That
/// exception is the <i>storage-level</i> signal for a duplicate <see cref="Envelope.Id" />, and
/// <c>DurableReceiver.handleDuplicateIncomingEnvelope</c> routes it through a redelivery-count check
/// that can dead-letter the message. A logical duplicate is not a delivery problem: the work was
/// already done, on purpose, and the correct outcome is a benign discard with an ack.
/// </para>
///
/// <para>
/// Returning early also means the executor records exactly one terminal event for this envelope
/// (<c>MessageSucceeded</c>), which is the invariant the tracking model depends on. Emitting a
/// second, discard-specific terminal record here would produce two terminal events on one path,
/// which cannot be ordered.
/// </para>
///
/// <para>
/// The discard is not silent — <see cref="MessageDeduplicator" /> logs it at Information — because a
/// duplicate that vanishes without a trace is indistinguishable from a message that was lost.
/// </para>
/// </summary>
internal class HandlerDeduplicationStopFrame : SyncFrame
{
    private readonly Variable _condition;

    public HandlerDeduplicationStopFrame(Variable condition)
    {
        _condition = condition;
        uses.Add(condition);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(
            "GH-4180: this logical deduplication id has already been handled, so discard this message");

        if (method.AsyncMode == AsyncMode.AsyncTask)
        {
            writer.Write($"if ({_condition.Usage}) return;");
        }
        else
        {
            writer.Write(
                $"if ({_condition.Usage}) return {typeof(Task).FullNameInCode()}.{nameof(Task.CompletedTask)};");
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// GH-4180. The message-handler answer to a <i>required</i> logical deduplication id that never
/// arrived.
///
/// <para>
/// Throws, rather than discarding. A missing id is the opposite of a duplicate: nothing has been
/// done and nothing will be, so treating it as benign would silently drop real work. Throwing hands
/// it to the ordinary error policies, which means it retries and ultimately dead-letters with a
/// message that names the cause — the visible failure a misconfigured publisher deserves.
/// </para>
/// </summary>
internal class MissingDeduplicationIdFrame : SyncFrame
{
    private readonly Variable _condition;
    private readonly string _description;

    public MissingDeduplicationIdFrame(Variable condition, string description)
    {
        _condition = condition;
        _description = description.Replace("\"", "'");
        uses.Add(condition);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.Write($"BLOCK:if ({_condition.Usage})");
        writer.Write(
            $"throw new {typeof(MissingDeduplicationIdException).FullNameInCode()}(\"{_description}\");");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }
}
