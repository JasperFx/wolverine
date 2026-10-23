using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Persistence.Codegen;

/// <summary>
/// GH-5133. Scopes a message handler's logical deduplication id to its own handler chain when the message type
/// has more than one chain -- one per handler type under <c>MultipleHandlerBehavior.Separated</c>, or sticky
/// handlers. Without it every chain claims the same id, the first claim wins, and the other handlers drop the
/// message as a duplicate of work they never did.
/// </summary>
/// <remarks>
/// The suffix is a short, stable hash of the handler type name rather than the name itself, because the
/// relational deduplication tables hold the id in a <c>varchar(250)</c>. A message type with a single chain never
/// gets this frame, so its ids are stored exactly as they always were.
/// </remarks>
[FSharpEmit(Skip = true,
    Reason = "Paired with DeduplicationIdFromEnvelopeFrame, which emits C# only. Logical deduplication is opt-in " +
             "per chain and unreachable in an F# handler chain that has not opted in.")]
internal class ScopeHandlerDeduplicationIdFrame : SyncFrame
{
    private readonly Variable _deduplicationId;
    private readonly string _scope;

    public ScopeHandlerDeduplicationIdFrame(Variable deduplicationId, string scope)
    {
        _deduplicationId = deduplicationId;
        _scope = scope;
        Variable = new Variable(typeof(string), "chainDeduplicationId", this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment("GH-5133: this message type has more than one handler chain, so the deduplication id is scoped to this one");
        writer.Write(
            $"var {Variable.Usage} = string.IsNullOrWhiteSpace({_deduplicationId.Usage}) ? {_deduplicationId.Usage} : {_deduplicationId.Usage} + \"{_scope}\";");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield return _deduplicationId;
    }

    /// <summary>
    /// The suffix appended to the id for a chain with these handler type names: "|h:" plus eight hex digits of
    /// a 32-bit FNV-1a hash. Deterministic across processes and machines, unlike string.GetHashCode().
    /// </summary>
    internal static string SuffixFor(string handlerTypeNames)
    {
        unchecked
        {
            var hash = 2166136261;
            foreach (var c in handlerTypeNames)
            {
                hash ^= c;
                hash *= 16777619;
            }

            return $"|h:{hash:x8}";
        }
    }
}
