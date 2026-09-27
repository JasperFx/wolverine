using System.Diagnostics;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Runtime;
using Wolverine.Runtime.Recurring;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// GH-4647. The recurring-message agent's tick loop is a long-lived <c>Task.Run</c>, and
/// <c>Task.Run</c> flows the <see cref="ExecutionContext" /> — so the loop used to capture whatever
/// <see cref="Activity.Current" /> was set when the agent STARTED and keep it for the life of the
/// host. Every occurrence then stamped that stale activity's id onto its envelope as
/// <see cref="Envelope.ParentId" /> (and its trace id as the <see cref="Envelope.CorrelationId" />),
/// so a startup-time span parented every recurring handler execution the process ever ran.
/// </summary>
/// <remarks>
/// <para>
/// Not hypothetical: in <see cref="DurabilityMode.Solo" /> the agent is started from inside the
/// startup <c>wolverine_node_assignments</c> activity, and the cluster-mode local auto-restart path
/// runs under the same span. The storeless host used here starts the agent directly, so the test
/// supplies its own startup-scoped activity to stand in for that one.
/// </para>
/// <para>
/// The fix is two-fold and both halves are asserted: context flow is suppressed when the loop is
/// scheduled (the GH-3518 pattern), so nothing ambient at start can reach it; and each occurrence is
/// published inside its own short-lived <c>wolverine.recurring.occurrence</c> activity, so every
/// firing is a bounded trace root that the envelope's parent id points at.
/// </para>
/// </remarks>
public class recurring_occurrences_are_their_own_trace_roots : IDisposable
{
    private readonly ActivitySource _source = new("CoreTests.gh-4647");
    private readonly ActivityListener _listener;
    private readonly List<Activity> _occurrenceSpans = new();

    public recurring_occurrences_are_their_own_trace_roots()
    {
        // Without a listener that samples, StartActivity returns null everywhere: there would be
        // no startup activity to inherit and no occurrence activity to find, and every assertion
        // below would pass vacuously.
        _listener = new ActivityListener
        {
            ShouldListenTo = x => x.Name == "Wolverine" || x.Name == _source.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                // Other hosts in the same CoreTests run publish their own occurrences; keep only
                // this class's schedule.
                if (activity.OperationName == WolverineTracing.RecurringOccurrence &&
                    activity.GetTagItem(WolverineTracing.ScheduleName) is string name &&
                    name == nameof(TracedRecurringMessage))
                {
                    lock (_occurrenceSpans)
                    {
                        _occurrenceSpans.Add(activity);
                    }
                }
            }
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    [Fact]
    public async Task occurrences_are_not_parented_under_the_activity_current_when_the_agent_started()
    {
        TracedRecurringMessageHandler.Reset();

        IHost host;
        Activity startup;

        // The agent is started while "startup" is the ambient activity, exactly as Solo mode starts
        // it inside wolverine_node_assignments. The activity ends before the first occurrence fires.
        using (startup = _source.StartActivity("startup")!)
        {
            // The guard for the guard: no ambient activity, nothing to inherit, nothing proven.
            startup.ShouldNotBeNull();

            host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Process-pinned by whichever host starts first (GH-3521); pin it so the handler
                    // below is discovered and the occurrence has somewhere to go.
                    opts.ApplicationAssembly = typeof(recurring_occurrences_are_their_own_trace_roots).Assembly;

                    // Every five seconds, the minimum legal cadence, so two real occurrences fire
                    // within the window below and the per-occurrence claim is about more than one.
                    opts.Schedules.ScheduleRecurring<TracedRecurringMessage>("*/5 * * * * *");
                }).StartAsync(TestContext.Current.CancellationToken);
        }

        using (host)
        {
            // Generous by design, like the sibling storeless tests: a full CoreTests run keeps ~40
            // hosts busy and the in-memory scheduler can fire well past the cron instant.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
            while (TracedRecurringMessageHandler.Count < 2 && DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(100, TestContext.Current.CancellationToken);
            }

            var envelopes = TracedRecurringMessageHandler.Snapshot();
            envelopes.Length.ShouldBeGreaterThanOrEqualTo(2, "fewer than two occurrences were handled within 90s");

            foreach (var envelope in envelopes)
            {
                // The bug: ParentId was the startup activity's id, and the bus's correlation id was
                // its trace id, for every occurrence forever.
                envelope.ParentId.ShouldNotBeNull("each occurrence is published inside its own activity");
                envelope.ParentId.Contains(startup.TraceId.ToHexString()).ShouldBeFalse(
                    "the occurrence inherited the activity that was current when the agent started");
                envelope.CorrelationId.ShouldNotBe(startup.RootId,
                    "the occurrence's correlation id was derived from the stale startup activity");
            }

            // Each firing is its own trace, not two spans of one.
            envelopes.Select(x => traceIdOf(x.ParentId!)).Distinct().Count().ShouldBe(envelopes.Length);
        }
    }

    [Fact]
    public async Task each_occurrence_is_published_inside_a_bounded_root_span_carrying_the_schedule_identity()
    {
        TracedRecurringMessageHandler.Reset();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(recurring_occurrences_are_their_own_trace_roots).Assembly;
                opts.Schedules.ScheduleRecurring<TracedRecurringMessage>("*/5 * * * * *");
            }).StartAsync(TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(90);
        while (TracedRecurringMessageHandler.Count < 2 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var envelopes = TracedRecurringMessageHandler.Snapshot();
        envelopes.Length.ShouldBeGreaterThanOrEqualTo(2, "fewer than two occurrences were handled within 90s");

        Activity[] spans;
        lock (_occurrenceSpans)
        {
            spans = _occurrenceSpans.ToArray();
        }

        spans.Length.ShouldBeGreaterThanOrEqualTo(2, "no wolverine.recurring.occurrence span was recorded");

        foreach (var span in spans)
        {
            // A root: the loop has nothing ambient to nest under, and the span must not outlive the
            // publish it wraps, or the next occurrence would nest under it.
            span.Parent.ShouldBeNull();
            span.Duration.ShouldBeLessThan(TimeSpan.FromSeconds(5));

            // The same identity the handler's execution span carries, so the two can be joined.
            span.GetTagItem(WolverineTracing.ScheduleName).ShouldBe(nameof(TracedRecurringMessage));
            var occurrence = span.GetTagItem(WolverineTracing.ScheduleOccurrence).ShouldBeOfType<string>();
            DateTimeOffset.Parse(occurrence).Offset.ShouldBe(TimeSpan.Zero);
        }

        // And the envelope's parent id points at that span, so the handler execution joins the
        // occurrence's trace rather than starting a new one.
        var spanIds = spans.Select(x => x.Id).ToHashSet();
        envelopes.ShouldAllBe(x => spanIds.Contains(x.ParentId!));
    }

    private static string traceIdOf(string w3cId)
    {
        // "00-<trace id>-<span id>-<flags>"
        return w3cId.Split('-')[1];
    }
}

public class TracedRecurringMessage;

public static class TracedRecurringMessageHandler
{
    private static readonly List<Envelope> _envelopes = new();

    public static int Count
    {
        get
        {
            lock (_envelopes)
            {
                return _envelopes.Count;
            }
        }
    }

    public static void Reset()
    {
        lock (_envelopes)
        {
            _envelopes.Clear();
        }
    }

    public static Envelope[] Snapshot()
    {
        lock (_envelopes)
        {
            return _envelopes.ToArray();
        }
    }

    public static void Handle(TracedRecurringMessage message, Envelope envelope)
    {
        lock (_envelopes)
        {
            _envelopes.Add(envelope);
        }
    }
}
