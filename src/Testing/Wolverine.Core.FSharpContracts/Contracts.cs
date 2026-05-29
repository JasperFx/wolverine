using Wolverine.Runtime;

namespace Wolverine.Core.FSharpContracts;

/// <summary>
///     The "milestone 0" contract for the F# code-generation foundation (issue GH-2969). A generated
///     adapter implements this interface; the foundation fixture exercises the smallest possible real
///     Wolverine frame (<see cref="Wolverine.Runtime.Handlers.MessageContextFrame" />) emitting F#.
/// </summary>
/// <remarks>
///     <see cref="Run" /> takes the <see cref="IWolverineRuntime" /> as a method argument purely so the
///     hand-built <c>GeneratedAssembly</c> can resolve it as an in-scope variable for
///     <c>MessageContextFrame</c> without standing up the full handler-discovery pipeline. Phase A swaps
///     this hand-built assembly for a real <c>HandlerGraph</c> rendering and richer F# handlers.
/// </remarks>
public interface IFoundationProbe
{
    void Run(IWolverineRuntime runtime);
}
