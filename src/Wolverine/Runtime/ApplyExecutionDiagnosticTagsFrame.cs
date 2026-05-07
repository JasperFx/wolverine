using System.Diagnostics;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime;

/// <summary>
/// Codegen frame inserted at the start of the generated handler chain when
/// <c>WolverineOptions.Tracking.HandlerExecutionDiagnosticsEnabled</c> is set
/// at codegen time. Emits a fully qualified static call to
/// <see cref="WolverineTracing.ApplyExecutionDiagnosticTags"/> so the
/// <c>wolverine.envelope.transport_lag_ms</c> and
/// <c>wolverine.envelope.receive_dwell_ms</c> tags get stamped onto the
/// active activity inline — no runtime <c>if/then</c> in the framework
/// <c>Executor</c> / <c>HandlerPipeline</c>. When the flag is off, the
/// frame isn't emitted into the chain at all and the helper is never
/// called.
/// </summary>
internal class ApplyExecutionDiagnosticTagsFrame : Frame
{
    private Variable? _envelope;

    public ApplyExecutionDiagnosticTagsFrame() : base(false)
    {
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _envelope = chain.FindVariable(typeof(Envelope));
        yield return _envelope;
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // Calls Wolverine.Runtime.WolverineTracing.ApplyExecutionDiagnosticTags(
        //     System.Diagnostics.Activity.Current, envelope);
        // The helper short-circuits on a null activity, so we don't need a guard here.
        writer.Write(
            $"{typeof(WolverineTracing).FullNameInCode()}.{nameof(WolverineTracing.ApplyExecutionDiagnosticTags)}(" +
            $"{typeof(Activity).FullNameInCode()}.{nameof(Activity.Current)}, {_envelope!.Usage});");

        Next?.GenerateCode(method, writer);
    }
}
