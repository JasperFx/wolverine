using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Xunit;

namespace CoreTests.Runtime.Agents;

public class AgentWorkConfirmationTests
{
    private static readonly Uri A = new("fake://one");
    private static readonly Uri B = new("fake://two");
    private static readonly Uri C = new("fake://three");

    private static Task<Uri[]> awaitCore(Func<Uri[], Task<Uri[]>> queryRunning, Task<Uri[]> workReply,
        Uri[] requested, AgentPresenceDirection direction, TimeSpan? stallTimeout = null,
        CancellationToken cancellation = default)
    {
#pragma warning disable VSTHRD003
        return AgentWorkConfirmation.AwaitCoreAsync(queryRunning, workReply, requested, direction,
            pollInterval: 25.Milliseconds(), stallTimeout ?? 60.Seconds(), NullLogger.Instance, cancellation);
#pragma warning restore VSTHRD003
    }

    private static Task<Uri[]> neverQueried(Uri[] _)
        => throw new InvalidOperationException("The destination should not have been polled");

    [Fact]
    public async Task the_reply_wins_when_it_arrives_first()
    {
        var confirmed = await awaitCore(neverQueried, Task.FromResult(new[] { A, B, C }), [A, B, C],
            AgentPresenceDirection.Running, cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([A, B, C], ignoreOrder: true);
    }

    [Fact]
    public async Task a_partial_reply_is_returned_as_is()
    {
        var confirmed = await awaitCore(neverQueried, Task.FromResult(new[] { B }), [A, B, C],
            AgentPresenceDirection.Running, cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([B]);
    }

    [Fact]
    public async Task polls_confirm_the_whole_batch_without_waiting_for_the_reply()
    {
        // The reply never arrives -- the batch is fully confirmed by observation alone
        var reply = new TaskCompletionSource<Uri[]>();

        var polls = 0;
        var confirmed = await awaitCore(_ =>
        {
            polls++;
            // Progressive convergence: one more agent is running on each poll
            return Task.FromResult(new[] { A, B, C }.Take(polls).ToArray());
        }, reply.Task, [A, B, C], AgentPresenceDirection.Running, cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([A, B, C], ignoreOrder: true);
        polls.ShouldBe(3);
    }

    [Fact]
    public async Task a_timed_out_reply_keeps_what_the_polls_observed_after_progress_stalls()
    {
        var reply = new TaskCompletionSource<Uri[]>();

        var polls = 0;
        var confirmed = await awaitCore(_ =>
        {
            polls++;
            if (polls == 2)
            {
                // The legacy reply window elapses after the second poll
                reply.SetException(new TimeoutException());
            }

            return Task.FromResult(new[] { A });
        }, reply.Task, [A, B, C], AgentPresenceDirection.Running, stallTimeout: 200.Milliseconds(),
            cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([A]);
    }

    [Fact]
    public async Task a_timed_out_reply_does_not_cut_short_a_destination_still_making_progress()
    {
        // The reply window elapses early, but the destination keeps answering polls and keeps making
        // progress -- the wait must continue to full confirmation on the polls alone
        var reply = new TaskCompletionSource<Uri[]>();

        var polls = 0;
        var confirmed = await awaitCore(_ =>
        {
            polls++;
            if (polls == 1)
            {
                reply.SetException(new TimeoutException());
            }

            return Task.FromResult(new[] { A, B, C }.Take(polls).ToArray());
        }, reply.Task, [A, B, C], AgentPresenceDirection.Running, stallTimeout: 60.Seconds(),
            cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([A, B, C], ignoreOrder: true);
    }

    [Fact]
    public async Task gone_mode_confirms_the_agents_absent_from_the_node()
    {
        // Waiting on stops: A is still running (stop not finished), B and C are gone
        var reply = new TaskCompletionSource<Uri[]>();

        var confirmed = await awaitCore(_ =>
        {
            var result = Task.FromResult(new[] { A });
            reply.SetException(new TimeoutException());
            return result;
        }, reply.Task, [A, B, C], AgentPresenceDirection.Gone, stallTimeout: 200.Milliseconds(),
            cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([B, C], ignoreOrder: true);
    }

    [Fact]
    public async Task gives_up_after_answered_polls_show_no_progress_for_the_stall_timeout()
    {
        var reply = new TaskCompletionSource<Uri[]>();

        var confirmed = await awaitCore(_ => Task.FromResult(Array.Empty<Uri>()), reply.Task, [A, B, C],
            AgentPresenceDirection.Running, stallTimeout: 150.Milliseconds(), cancellation: TestContext.Current.CancellationToken);

        // Gave up on a wedged destination without waiting for the (never-arriving) reply
        confirmed.ShouldBeEmpty();
    }

    [Fact]
    public async Task progress_resets_the_stall_clock()
    {
        var reply = new TaskCompletionSource<Uri[]>();

        // Each agent takes ~100ms to appear -- longer than the 150ms stall timeout would allow if the
        // clock never reset, but each poll that confirms something new buys more patience
        var started = DateTimeOffset.UtcNow;
        var confirmed = await awaitCore(_ =>
        {
            var elapsed = DateTimeOffset.UtcNow - started;
            var running = new[] { A, B, C }.Take((int)(elapsed.TotalMilliseconds / 100)).ToArray();
            return Task.FromResult(running);
        }, reply.Task, [A, B, C], AgentPresenceDirection.Running, stallTimeout: 150.Milliseconds(), cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([A, B, C], ignoreOrder: true);
    }

    [Fact]
    public async Task unanswered_polls_never_trip_the_stall_timeout()
    {
        // A destination that cannot answer the presence query -- a pre-upgrade node -- leaves the
        // reply's own window as the only bound, so a tiny stall timeout must NOT cut the wait short
        var reply = new TaskCompletionSource<Uri[]>();

        var polls = 0;
        var confirmed = await awaitCore(_ =>
        {
            polls++;
            if (polls == 10)
            {
                // The real reply finally lands long after the stall timeout would have expired
                reply.SetResult([A, B]);
            }

            throw new TimeoutException("no such handler on this node");
        }, reply.Task, [A, B, C], AgentPresenceDirection.Running, stallTimeout: 50.Milliseconds(), cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([A, B], ignoreOrder: true);
        polls.ShouldBeGreaterThanOrEqualTo(10);
    }

    [Fact]
    public async Task cancellation_returns_whatever_was_confirmed()
    {
        var reply = new TaskCompletionSource<Uri[]>();
        using var cancellation = new CancellationTokenSource();

        var confirmed = await awaitCore(async _ =>
        {
            await cancellation.CancelAsync();
            return [A];
        }, reply.Task, [A, B, C], AgentPresenceDirection.Running, cancellation: cancellation.Token);

        confirmed.ShouldBe([A]);
    }

    [Fact]
    public async Task confirmations_outside_the_requested_set_are_ignored()
    {
        var other = new Uri("fake://someone-else");
        var reply = new TaskCompletionSource<Uri[]>();

        var confirmed = await awaitCore(_ =>
        {
            var result = Task.FromResult(new[] { A, other });
            reply.SetException(new TimeoutException());
            return result;
        }, reply.Task, [A, B], AgentPresenceDirection.Running, stallTimeout: 200.Milliseconds(),
            cancellation: TestContext.Current.CancellationToken);

        confirmed.ShouldBe([A]);
    }
}

public class QueryAgentPresenceTests
{
    [Fact]
    public async Task reports_the_intersection_of_requested_and_running()
    {
        var runtime = new MockWolverineRuntime();
        var a = new Uri("fake://one");
        var b = new Uri("fake://two");
        var c = new Uri("fake://three");

        runtime.Agents.AllRunningAgentUris().Returns([a, b]);

        var commands = await new QueryAgentPresence([b, c]).ExecuteAsync(runtime, CancellationToken.None);

        commands.Single().ShouldBeOfType<AgentPresenceReport>().Running.ShouldBe([b]);
    }

    [Fact]
    public async Task reports_nothing_running_when_nothing_matches()
    {
        var runtime = new MockWolverineRuntime();
        runtime.Agents.AllRunningAgentUris().Returns([new Uri("fake://other")]);

        var commands = await new QueryAgentPresence([new Uri("fake://one")])
            .ExecuteAsync(runtime, CancellationToken.None);

        commands.Single().ShouldBeOfType<AgentPresenceReport>().Running.ShouldBeEmpty();
    }
}
