using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using Wolverine.Configuration;

namespace Wolverine.Persistence.Codegen;

/// <summary>
/// GH-4180. Reads the logical deduplication id straight off the incoming <see cref="Envelope" />.
/// This is the default id source for message handlers, corresponding to a publisher that set
/// <c>DeliveryOptions.DeduplicationId</c> (or <c>Envelope.DeduplicationId</c> directly).
///
/// <para>
/// <see cref="Envelope.DeduplicationId" /> already round-trips on every transport — it is written by
/// <c>EnvelopeSerializer</c> under the <c>deduplication-id</c> wire header and read back on the far
/// side — so nothing transport-specific is needed here, including for the two transports that also
/// consume it natively (SQS/SNS FIFO and GCP Pub/Sub).
/// </para>
/// </summary>
[FSharpEmit(Skip = true,
    Reason = "Emits a null-conditional read into a C# local. Logical deduplication is opt-in per chain " +
             "and unreachable in an F# handler chain that has not opted in.")]
internal class DeduplicationIdFromEnvelopeFrame : SyncFrame
{
    public DeduplicationIdFromEnvelopeFrame()
    {
        Variable = new Variable(typeof(string), "deduplicationId", this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Null-conditional rather than `!`: an envelope is always present on the listening path, but
        // this same generated handler is reachable through IMessageBus.InvokeAsync() in tests, where a
        // NullReferenceException here would be a very confusing way to learn that.
        writer.Write($"var {Variable.Usage} = context.Envelope?.{nameof(Envelope.DeduplicationId)};");
        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield break;
    }
}
