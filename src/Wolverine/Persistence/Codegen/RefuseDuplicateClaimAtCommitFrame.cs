using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Persistence.Codegen;

/// <summary>
/// GH-4505. Turns the one duplicate an optimistic check cannot catch — two concurrent callers, both told
/// "not claimed", both queueing the INSERT — into the same refusal the check produces.
///
/// <para>
/// Without this the loser of that race escapes as a raw unique-constraint violation: a 500 on an HTTP
/// endpoint, a dead-letter on a handler. With it, the caller gets the endpoint's configured 409 (or the
/// handler discards) exactly as if the check had won. That is the price of the optimistic pattern, and it
/// is worth saying plainly: the loser does its work and throws it away at commit, where claim-and-release
/// refuses it before doing any.
/// </para>
///
/// <para>
/// The refusal is written AFTER the try block, not inside the catch, and that ordering is load-bearing on
/// HTTP: <c>SaveChangesAsync</c> is a postprocessor and the response writer is appended after it, so the
/// commit fails while <c>Response.HasStarted</c> is still false and a clean problem document can still go
/// out.
/// </para>
///
/// <para>
/// Store-agnostic because the only per-store part is which exception counts. Every provider that can write
/// a claim inside its own transaction needs exactly this frame; only
/// <see cref="TransactionalDeduplication.CommitRaceWrapper" />'s classifier changes.
/// </para>
/// </summary>
public class RefuseDuplicateClaimAtCommitFrame : Frame
{
    private readonly Frame[] _refusal;
    private readonly string _classifierUsage;

    /// <param name="classifier">
    /// A static predicate taking the caught <see cref="Exception" /> and answering whether the commit lost
    /// a race for a deduplication id. Rendered into an exception filter, so it MUST be scoped to the
    /// deduplication table — a blanket "any unique violation" test would swallow a duplicate-key failure
    /// raised by the application's own data and report it to the caller as "already handled".
    /// </param>
    /// <param name="buildRefusal">
    /// Builds the chain-type-specific refusal from the <c>bool</c> this frame produces — normally
    /// <c>chain.BuildDeduplicationStopCondition(...)</c>, so the refusal is identical to the one the
    /// up-front check emits.
    /// </param>
    public RefuseDuplicateClaimAtCommitFrame(MethodInfo classifier, Func<Variable, Frame[]> buildRefusal)
        : base(true)
    {
        _classifierUsage = $"{classifier.DeclaringType!.FullNameInCode()}.{classifier.Name}";
        LostTheRace = new Variable(typeof(bool), "duplicateClaimLostTheRace", this);
        _refusal = buildRefusal(LostTheRace);
    }

    /// <summary>Set when the commit tripped the deduplication table's primary key.</summary>
    public Variable LostTheRace { get; }

    /// <summary>
    /// Name of the caught exception. Deliberately not <c>e</c>: this frame nests inside whatever the rest
    /// of the chain generates, and a one-letter name is the one most likely to collide with a handler
    /// parameter or another frame's variable.
    /// </summary>
    private const string ExceptionName = "duplicateClaimException";

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Nothing to convert the failure INTO means the catch would swallow a real error and report the
        // work as done. Generate no wrapper at all rather than that. Unreachable for every chain type
        // Wolverine ships -- the base BuildDeduplicationStopCondition throws rather than answering empty --
        // but the failure mode is silent enough to be worth refusing structurally.
        if (_refusal.Length == 0)
        {
            Next?.GenerateCode(method, writer);
            return;
        }

        writer.Write($"var {LostTheRace.Usage} = false;");
        writer.Write("BLOCK:try");
        Next?.GenerateCode(method, writer);
        writer.FinishBlock();

        // An exception filter rather than a test inside the catch body, so an unrelated failure is never
        // caught and rethrown -- a rethrow from here would push the original stack trace one frame deeper
        // for every exception the error policies are supposed to see unaltered.
        writer.Write(
            $"BLOCK:catch ({typeof(Exception).FullNameInCode()} {ExceptionName}) when ({_classifierUsage}({ExceptionName}))");
        writer.WriteComment("GH-4505: a concurrent caller committed this logical id first");
        writer.Write($"{LostTheRace.Usage} = true;");
        writer.FinishBlock();

        for (var i = 0; i < _refusal.Length - 1; i++)
        {
            _refusal[i].Next = _refusal[i + 1];
        }

        _refusal[^1].Next = null;
        _refusal[0].GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        foreach (var frame in _refusal)
        {
            foreach (var variable in frame.FindVariables(chain))
            {
                yield return variable;
            }
        }
    }
}
