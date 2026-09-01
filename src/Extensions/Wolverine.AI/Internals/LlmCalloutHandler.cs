using Wolverine.Attributes;

namespace Wolverine.AI.Internals;

/// <summary>
/// The one handler for every LLM callout in the application. Registered explicitly by
/// <c>AddLlmCallouts</c> rather than found by conventional discovery, since it lives in
/// WolverineFx.AI rather than in the application's own assemblies.
/// </summary>
/// <remarks>
/// Returning <see cref="object" /> makes the answer an ordinary cascading message: Wolverine routes and
/// publishes it exactly as if a handler had returned that type directly, which is what keeps the
/// response side of a callout indistinguishable from any other message. The cost is that Wolverine
/// cannot know statically what this chain publishes, so the response types do not appear in
/// <c>PublishedTypes()</c> or in routing preview output.
/// </remarks>
[WolverineHandler]
public static class LlmCalloutHandler
{
    public static Task<object> HandleAsync(LlmCallout callout, ILlmCalloutExecutor executor,
        CancellationToken cancellationToken)
    {
        return executor.ExecuteAsync(callout, cancellationToken);
    }
}
