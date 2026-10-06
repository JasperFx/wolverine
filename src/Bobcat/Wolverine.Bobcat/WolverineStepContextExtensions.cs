using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.Extensions.Hosting;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

/// <summary>
/// Extension methods for IStepContext that delegate to Wolverine's message tracking APIs.
/// These work against any registered IHostResource (HostResource, AlbaResource, etc)
/// so fixtures do not need to reference a Wolverine-specific resource type.
/// </summary>
public static class WolverineStepContextExtensions
{
    // Every helper below opens a tracked session, and each resolves its host here — so this is
    // where handler compilation is moved out of the session's timeout window (bobcat#287).
    private static IHost getWolverineHost(IStepContext context, string? resourceName)
    {
        var host = context.GetResource<IHostResource>(resourceName).Host;
        HandlerWarmUp.WarmBeforeTracking(host);
        return host;
    }

    /// <summary>
    /// Invoke a message and wait for it and all cascading messages to complete.
    /// </summary>
    public static Task<ITrackedSession> InvokeMessageAndWaitAsync(
        this IStepContext context,
        object message,
        string? resourceName = null,
        int timeoutInMilliseconds = 5000)
        => getWolverineHost(context, resourceName)
            .InvokeMessageAndWaitAsync(message, timeoutInMilliseconds);

    /// <summary>
    /// Invoke a message expecting a return value, and wait for all cascading messages to complete.
    /// </summary>
    public static Task<(ITrackedSession, T?)> InvokeMessageAndWaitAsync<T>(
        this IStepContext context,
        object message,
        string? resourceName = null,
        int timeoutInMilliseconds = 5000)
        => getWolverineHost(context, resourceName)
            .InvokeMessageAndWaitAsync<T>(message, timeoutInMilliseconds);

    /// <summary>
    /// Send a message and wait for it and all cascading messages to complete.
    /// </summary>
    public static Task<ITrackedSession> SendMessageAndWaitAsync<T>(
        this IStepContext context,
        T message,
        string? resourceName = null,
        int timeoutInMilliseconds = 5000)
        => getWolverineHost(context, resourceName)
            .SendMessageAndWaitAsync(message, null, timeoutInMilliseconds);

    /// <summary>
    /// Start a tracked activity session on the Wolverine host for manual coordination.
    /// </summary>
    public static TrackedSessionConfiguration TrackActivity(
        this IStepContext context,
        string? resourceName = null)
        => getWolverineHost(context, resourceName).TrackActivity();

    /// <summary>
    /// Run any action — an Alba HTTP scenario, a client call, anything that reaches the
    /// application from the outside — inside Wolverine's tracked session, and wait for every
    /// message the action caused (cascades, forwarded events, local queues) to settle before
    /// returning the session's full record. Wolverine's <c>TrackedHttpCall</c> pattern with the
    /// call itself supplied as a delegate, so this package needs no HTTP dependency (bobcat#211).
    /// </summary>
    public static Task<ITrackedSession> ExecuteAndWaitAsync(
        this IStepContext context,
        Func<Task> action,
        string? resourceName = null,
        int timeoutInMilliseconds = 5000)
        => getWolverineHost(context, resourceName).ExecuteAndWaitAsync(action, timeoutInMilliseconds);
}
