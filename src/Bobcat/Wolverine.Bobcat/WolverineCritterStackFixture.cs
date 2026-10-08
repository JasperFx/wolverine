using Bobcat.CritterStack;
using Wolverine;
using Wolverine.Tracking;

namespace Wolverine.Bobcat;

/// <summary>
/// A tracked Wolverine session, as the store grammar reads it.
/// </summary>
public sealed class WolverineActOutcome(ITrackedSession session) : IActOutcome
{
    /// <summary>The session itself, for a step that wants Wolverine's own richer API.</summary>
    public ITrackedSession Session { get; } = session;

    public IReadOnlyList<object> MessagesSent { get; } = session.Sent.AllMessages().ToList();
}

/// <summary>
/// The Critter Stack Gherkin vocabulary with Wolverine performing the act.
/// </summary>
/// <remarks>
/// <para>
/// Everything a scenario says — arranging events, asserting what was emitted, asserting a read
/// model — lives in <see cref="CritterStackFixture"/> in Bobcat core and runs against the
/// JasperFx.Events abstractions. This class adds the one thing core cannot do: send the command
/// and wait for everything it caused to settle.
/// </para>
/// <para>
/// Derive from it and the shipped grammar is your whole fixture:
/// <code>public class FreezeAccountFixture : WolverineCritterStackFixture;</code>
/// </para>
/// </remarks>
public abstract class WolverineCritterStackFixture : CritterStackFixture
{
    /// <inheritdoc />
    protected override async Task<IActOutcome> DispatchAsync(object command, int timeoutInMilliseconds)
        => new WolverineActOutcome(
            await Ctx.InvokeMessageAndWaitAsync(command, HostResource, timeoutInMilliseconds));

    /// <summary>
    /// Act: run any call that reaches the application from the outside — an Alba HTTP scenario, a
    /// SignalR client, a gRPC call — inside Wolverine's tracked session, waiting for everything the
    /// call <i>caused</i> before capturing the outcome.
    /// </summary>
    /// <remarks>
    /// A bare HTTP call returns when the response does, while the downstream work is still in
    /// flight; this one returns when the work has landed, so the whole assertion vocabulary works
    /// unchanged after an HTTP act. The call is a delegate, so this package needs no HTTP
    /// dependency — pair it with <c>Bobcat.Alba</c>:
    /// <c>await WhenTracked(() =&gt; Context.PostJsonAsync&lt;Req, Res&gt;(url, body))</c>.
    /// <para>
    /// <paramref name="configureTracking"/> exists for the call that only <i>enqueues</i> work: an
    /// endpoint handing envelopes to a local queue can return before the session observes any
    /// activity, and the session then completes on zero activity. Stating the expected executions
    /// up front closes that race — the same reason Wolverine's own sample grew the overload
    /// (wolverine GH-3714).
    /// </para>
    /// </remarks>
    public async Task<T?> WhenTracked<T>(
        Func<Task<T>> act,
        int timeoutInMilliseconds = 5000,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
    {
        T? result = default;

        await ExecuteActAsync(async () =>
        {
            var tracking = Ctx.TrackActivity(HostResource)
                .Timeout(TimeSpan.FromMilliseconds(timeoutInMilliseconds));
            if (configureTracking != null) tracking = configureTracking(tracking);

            Func<IMessageContext, Task> execution = async _ => result = await act();
            return new WolverineActOutcome(await tracking.ExecuteAndWaitAsync(execution));
        });

        return LastError == null ? result : default;
    }

    /// <inheritdoc cref="WhenTracked{T}(Func{Task{T}}, int, Func{TrackedSessionConfiguration, TrackedSessionConfiguration}?)"/>
    public Task WhenTracked(
        Func<Task> act,
        int timeoutInMilliseconds = 5000,
        Func<TrackedSessionConfiguration, TrackedSessionConfiguration>? configureTracking = null)
        => WhenTracked<object?>(async () =>
        {
            await act();
            return null;
        }, timeoutInMilliseconds, configureTracking);
}
