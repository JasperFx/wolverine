using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Transports;
using Wolverine.Transports.Stub;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Runtime.Agents;

/// <summary>
/// GH-4650. Wolverine's long-lived agent plumbing loops are started through
/// <see cref="DetachedTask" />, which does not flow the caller's <see cref="ExecutionContext" /> into
/// the loop -- so a loop never inherits whatever <see cref="Activity.Current" /> was set when it was
/// started, and neither does anything the loop constructs. The same defect as GH-3518, GH-4647 and
/// GH-4649 in the loops those did not reach.
/// </summary>
public class detached_agent_loops_4650 : IDisposable
{
    private readonly ActivitySource theSource = new("CoreTests.gh-4650");
    private readonly ActivityListener theListener;

    public detached_agent_loops_4650()
    {
        // Without a listener that samples, StartActivity returns null: there is no ambient activity
        // to inherit and every fact below passes vacuously.
        theListener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == theSource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };

        ActivitySource.AddActivityListener(theListener);
    }

    public void Dispose()
    {
        theListener.Dispose();
        theSource.Dispose();
    }

    /// <summary>
    /// The negative control: a plain <c>Task.Run</c> DOES carry the ambient activity across, which is
    /// what the helper exists to prevent. Without this fact, the ones below could pass because the
    /// harness cannot observe capture at all.
    /// </summary>
    [Fact]
    public async Task a_plain_task_run_inherits_the_ambient_activity()
    {
        var inside = new TaskCompletionSource<Activity?>();

        using var ambient = theSource.StartActivity("ambient");
        ambient.ShouldNotBeNull();

        _ = Task.Run(() => inside.TrySetResult(Activity.Current), TestContext.Current.CancellationToken);

        (await inside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            .ShouldBeSameAs(ambient);
    }

    [Fact]
    public async Task run_does_not_flow_the_ambient_activity_into_the_loop()
    {
        var inside = new TaskCompletionSource<Activity?>();

        using var ambient = theSource.StartActivity("ambient");
        ambient.ShouldNotBeNull();

        _ = DetachedTask.Run(() =>
        {
            inside.TrySetResult(Activity.Current);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        (await inside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    [Fact]
    public async Task run_long_running_does_not_flow_the_ambient_activity_into_the_loop()
    {
        var inside = new TaskCompletionSource<Activity?>();

        using var ambient = theSource.StartActivity("ambient");
        ambient.ShouldNotBeNull();

        _ = DetachedTask.RunLongRunning(() =>
        {
            inside.TrySetResult(Activity.Current);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        (await inside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    /// <summary>
    /// Suppressing flow is scoped to the start and undone before the helper returns, so the caller
    /// keeps its own activity and its own flow.
    /// </summary>
    [Fact]
    public async Task the_callers_own_activity_and_flow_survive()
    {
        var started = new TaskCompletionSource();

        using var ambient = theSource.StartActivity("ambient");
        ambient.ShouldNotBeNull();

        var loop = DetachedTask.Run(() =>
        {
            started.TrySetResult();
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await loop;

        Activity.Current.ShouldBeSameAs(ambient);
        ExecutionContext.IsFlowSuppressed().ShouldBeFalse();
    }

    /// <summary>
    /// <see cref="ExecutionContext.SuppressFlow" /> throws when flow is already suppressed, so a caller
    /// that is itself inside a suppressed scope must still be able to start a loop.
    /// </summary>
    [Fact]
    public async Task run_is_safe_when_flow_is_already_suppressed()
    {
        var inside = new TaskCompletionSource<Activity?>();
        Task loop;

        // The suppression scope has to be undone on the thread that created it, so start inside and
        // await outside.
        using (ExecutionContext.SuppressFlow())
        {
            loop = DetachedTask.Run(() =>
            {
                inside.TrySetResult(Activity.Current);
                return Task.CompletedTask;
            }, TestContext.Current.CancellationToken);
        }

        await loop;
        (await inside.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }

    /// <summary>
    /// The one loop of the three named in GH-4650 that can be built in isolation: the inbox health
    /// restarter, constructed inside an activity the way a retry-block worker's captured context would
    /// have constructed it. Its probe against the message store must not run under that activity.
    /// </summary>
    [Fact]
    public async Task the_inbox_health_probe_does_not_run_under_the_activity_current_at_construction()
    {
        var runtime = new MockWolverineRuntime();
        var observed = new TaskCompletionSource<Activity?>();

        runtime.Storage.Inbox.ReleaseIncomingAsync(Arg.Any<int>(), Arg.Any<Uri>())
            .Returns(_ =>
            {
                observed.TrySetResult(Activity.Current);
                return Task.CompletedTask;
            });

        var circuit = Substitute.For<IListenerCircuit>();
        circuit.Endpoint.Returns(new StubEndpoint("one", new StubTransport()));

        InboxHealthRestarter restarter;
        using (var startup = theSource.StartActivity("startup"))
        {
            // The guard for the guard: nothing ambient, nothing inherited, nothing proven.
            startup.ShouldNotBeNull();
            restarter = new InboxHealthRestarter(circuit, runtime, NullLogger.Instance);
        }

        using (restarter)
        {
            // The first probe fires after the loop's initial two-second back-off.
            (await observed.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken))
                .ShouldBeNull("the inbox probe ran under the activity current when the restarter was built");
        }
    }
}
