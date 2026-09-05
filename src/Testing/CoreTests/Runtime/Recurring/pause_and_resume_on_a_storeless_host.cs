using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Runtime;
using Wolverine.Runtime.Recurring;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// The agent-memory half of pause/resume — what <see cref="IRecurringScheduleControl" /> does on a
/// host whose message store has no recurring tracking extension. The durable half (eager cancel,
/// pause surviving restart, resume strictly-after-now against a real inbox) lives in the message
/// store compliance tier; what is pinned here is the documented DEGRADED behaviour: pause reaches
/// only the local agent's memory, stops future publishes, deliberately keeps the already-pending
/// occurrence's bookkeeping (an uncancellable envelope must not be re-published on resume), and
/// unknown names throw at the control surface regardless of store.
/// </summary>
public class pause_and_resume_on_a_storeless_host
{
    private static async Task<(IHost host, RecurringMessageAgent agent, IRecurringScheduleControl control)>
        buildAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // See recurring_messages_on_a_storeless_host for why the assembly pin matters (GH-3521).
                opts.ApplicationAssembly = typeof(pause_and_resume_on_a_storeless_host).Assembly;
                opts.Schedules.ScheduleRecurring<PendingRecurringMessage>("0 9 * * *");
            }).StartAsync(TestContext.Current.CancellationToken);

        var runtime = (WolverineRuntime)host.GetRuntime();
        var agent = runtime.InMemoryRecurringAgent.ShouldBeOfType<RecurringMessageAgent>();
        var control = host.Services.GetRequiredService<IRecurringScheduleControl>();

        // The startup publish of the (pending, daily) next occurrence.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (agent.OccurrencesPublished == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        agent.OccurrencesPublished.ShouldBe(1);
        return (host, agent, control);
    }

    [Fact]
    public async Task pause_stops_future_publishes_and_resume_restores_them()
    {
        var (host, agent, control) = await buildAsync();
        using var _ = host;

        await control.PauseAsync(nameof(PendingRecurringMessage), TestContext.Current.CancellationToken);

        // Two days pass — an unpaused schedule would compute a NEW next occurrence and publish it.
        var clock = new FrozenClock(DateTimeOffset.UtcNow.AddDays(2));
        agent.TimeProvider = clock;

        await agent.TickAsync(TestContext.Current.CancellationToken);
        agent.OccurrencesPublished.ShouldBe(1); // paused: nothing new

        await control.ResumeAsync(nameof(PendingRecurringMessage), TestContext.Current.CancellationToken);

        await agent.TickAsync(TestContext.Current.CancellationToken);
        agent.OccurrencesPublished.ShouldBe(2); // resumed: the occurrence strictly after "now"
    }

    [Fact]
    public async Task in_memory_pause_keeps_the_pending_occurrence_bookkeeping()
    {
        var (host, agent, control) = await buildAsync();
        using var _ = host;

        await control.PauseAsync(nameof(PendingRecurringMessage), TestContext.Current.CancellationToken);
        await control.ResumeAsync(nameof(PendingRecurringMessage), TestContext.Current.CancellationToken);

        // With no store, the pending occurrence could not be cancelled by the pause — so a
        // pause/resume round trip before it fires must NOT publish it a second time (there is no
        // store-backed deduplication here to collapse the duplicate).
        await agent.TickAsync(TestContext.Current.CancellationToken);
        agent.OccurrencesPublished.ShouldBe(1);
    }

    [Fact]
    public async Task unknown_schedule_names_throw_at_the_control_surface()
    {
        var (host, _, control) = await buildAsync();
        using var _1 = host;

        var ex = await Should.ThrowAsync<UnknownRecurringScheduleException>(
            () => control.PauseAsync("nope", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("nope");
        ex.Message.ShouldContain(nameof(PendingRecurringMessage));

        await Should.ThrowAsync<UnknownRecurringScheduleException>(
            () => control.ResumeAsync("nope", TestContext.Current.CancellationToken));
    }

    private sealed class FrozenClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now = _now.Add(by);
    }
}
