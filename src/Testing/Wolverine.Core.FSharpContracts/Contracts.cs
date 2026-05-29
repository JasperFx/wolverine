using Wolverine.Attributes;
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

// -----------------------------------------------------------------------------
// Phase A handler surface (issue GH-2969): the smallest in-process handler that
// exercises message extraction, simple validation (abort), and a cascading
// message. The driver discovers NameHandler, renders its real Wolverine handler
// chain to F#, and the fixture compiles the generated adapter against these
// public types. (Authoring handlers in F# is the separate concern of #2968;
// this audit only proves the codegen frames emit F#.)
// -----------------------------------------------------------------------------

/// <summary>The command handled by <see cref="NameHandler" />. The <c>[Audit]</c> member also
/// exercises <c>AuditToActivityFrame</c>.</summary>
public record CreateName([property: Audit] string Name);

/// <summary>The event cascaded back out by <see cref="NameHandler" />.</summary>
public record NameCreated(string Name);

/// <summary>
///     A minimal async in-process handler with simple validation + a cascading return. Produces, in
///     chain order: message extraction, OTel tags, a <c>Validate</c> continuation (abort-if-invalid),
///     the handler call, and a cascaded <see cref="NameCreated" />. Exercises the abort guard inside a
///     <c>task { }</c> body.
/// </summary>
public class NameHandler
{
    public IEnumerable<string> Validate(CreateName command)
    {
        return string.IsNullOrWhiteSpace(command.Name)
            ? new[] { "Name is required" }
            : Array.Empty<string>();
    }

    public NameCreated Handle(CreateName command)
    {
        return new NameCreated(command.Name);
    }
}

/// <summary>The command handled by <see cref="CheckThingHandler" />.</summary>
public record CheckThing(string Value);

/// <summary>
///     A synchronous handler whose <c>Before</c> returns a <see cref="RequirementResult" />, exercising
///     <c>RequirementResultHandlerFrame</c> and the non-<c>task { }</c> abort path (the method returns
///     <c>Task</c> and the abort branch yields <c>Task.CompletedTask</c>).
/// </summary>
public class CheckThingHandler
{
    public RequirementResult Before(CheckThing command)
    {
        return string.IsNullOrEmpty(command.Value)
            ? new RequirementResult(HandlerContinuation.Stop, new[] { "Value is required" })
            : RequirementResult.AllGood();
    }

    public void Handle(CheckThing command)
    {
    }
}

/// <summary>The command handled by <see cref="GateHandler" />.</summary>
public record Gate(bool Ok);

/// <summary>
///     A synchronous handler whose <c>Before</c> returns a <see cref="HandlerContinuation" />,
///     exercising <c>HandlerContinuationFrame</c>.
/// </summary>
public class GateHandler
{
    public HandlerContinuation Before(Gate command)
    {
        return command.Ok ? HandlerContinuation.Continue : HandlerContinuation.Stop;
    }

    public void Handle(Gate command)
    {
    }
}
