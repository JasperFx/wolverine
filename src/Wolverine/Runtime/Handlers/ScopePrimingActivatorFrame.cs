using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;

namespace Wolverine.Runtime.Handlers;

/// <summary>
/// Codegen-time activator (GH-3001). When a chain falls back to service location, the generated code
/// creates a child <c>IServiceScope</c> off the root provider. This frame detects that scope at
/// arrangement time and registers scoping postprocessors on it — always the
/// <see cref="PrimeScopedMessageContextFrame"/>, plus the frames produced by
/// <c>WolverineOptions.ScopingFrameSources</c> (integration-contributed, e.g. Marten's session
/// priming) — so they run immediately after the scope is created and prime it with the correct
/// already-resolved instances. Emits no code itself.
///
/// If no service-location scope is created for the chain (the IServiceProvider variable's Creator is
/// not an <see cref="IScopedContainerCreation"/>), nothing is attached.
/// </summary>
internal sealed class ScopePrimingActivatorFrame : SyncFrame
{
    private readonly IReadOnlyList<SyncFrame> _scopingFrames;

    public ScopePrimingActivatorFrame(IReadOnlyList<SyncFrame> scopingFrames)
    {
        _scopingFrames = scopingFrames;
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Non-forcing: only finds the scoped IServiceProvider that the service-location machinery
        // already created. Using FindVariable here would *inject* a root IServiceProvider into every
        // handler (flagging it as service location — which would fail under ServiceLocationPolicy.NotAllowed).
        var provider = chain.TryFindVariable(typeof(IServiceProvider), VariableSource.NotServices);
        if (provider?.Creator is IScopedContainerCreation scoped)
        {
            foreach (var frame in _scopingFrames)
            {
                scoped.AddPostProcessor(frame);
            }

            yield return provider;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        // No-op: the work is registering postprocessors on the scope during FindVariables.
        Next?.GenerateCode(method, writer);
    }

    // No-op for F# too — this frame emits no code in either language (it only registers postprocessors
    // during arrangement). Required so it doesn't hit the base Frame.GenerateFSharpCode which throws.
    public override void GenerateFSharpCode(GeneratedMethod method, ISourceWriter writer)
    {
        Next?.GenerateFSharpCode(method, writer);
    }
}
