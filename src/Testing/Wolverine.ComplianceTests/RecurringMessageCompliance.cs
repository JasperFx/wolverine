using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.ScheduledMessageManagement;
using Wolverine.RDBMS;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Recurring;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// Compliance facts for the recurring (cron) message tracking extension
/// (<see cref="IRecurringMessageStore" />) — every RDBMS-backed message store inherits these by
/// subclassing and supplying its persistence configuration. Anything asserting against a store
/// lives here rather than in a provider-specific suite, so a provider cannot quietly diverge.
///
/// <para>
/// The agent/loop logic itself (tick idempotence, skipped occurrences, storeless mode, the
/// Serverless/MediatorOnly refusals) is pinned in CoreTests; these facts cover what only a real
/// store can prove — the tracking row, verification against the durable inbox, durable
/// pause/resume, and the opt-in's schema neutrality.
/// </para>
/// </summary>
public abstract class RecurringMessageCompliance : IAsyncLifetime
{
    private readonly List<IHost> _hosts = [];
    private readonly List<RecurringMessageAgent> _agents = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _logProblems = new();

    /// <summary>Wire this provider's message persistence into the options.</summary>
    protected abstract void configurePersistence(WolverineOptions opts);

    public ValueTask InitializeAsync()
    {
        RecurringComplianceMessageHandler.Reset();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
            }
            finally
            {
                host.Dispose();
            }
        }
    }

    /// <summary>
    /// Build (and track for disposal) a Solo-mode host against this provider's store.
    /// <paramref name="clean" /> resets all resource state — pass false for the second host of a
    /// restart scenario, where surviving state is the thing under test.
    /// </summary>
    protected async Task<IHost> buildHost(Action<WolverineOptions> configure, bool clean = true)
    {
        if (clean)
        {
            // The recurring agent starts publishing the moment the real host starts, so any reset
            // must complete BEFORE that host exists — a reset issued after StartAsync races the
            // startup publish and wipes it out from under the agent's in-memory picture, which
            // then (correctly) refuses to publish the same occurrence again. A schedule-less
            // pre-flight host owns the reset; the durability flags are forced by hand so its
            // store knows about (provisions, migrates, clears) the recurring + deduplication
            // tables the real host is about to use.
            using var preFlight = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery();
                    opts.Durability.Mode = DurabilityMode.Solo;
                    opts.Durability.EnableRecurringMessages = true;
                    opts.Durability.EnableMessageDeduplication = true;
                    configurePersistence(opts);
                }).StartAsync(TestContext.Current.CancellationToken);

            await preFlight.ResetResourceState();
            await preFlight.StopAsync(TestContext.Current.CancellationToken);
        }

        var host = Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddProvider(new ProblemCapturingLoggerProvider(_logProblems)))
            .UseWolverine(opts =>
            {
                // Explicit inclusion rather than scanning: the compliance assembly carries
                // handlers for OTHER compliance suites (StorageActionCompliance's Todo handlers
                // among them) whose persistence this host deliberately does not configure, and
                // explicit inclusion is also immune to the GH-3521 first-host-wins assembly pin.
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(RecurringComplianceMessageHandler));

                // Solo assigns every agent to this node immediately, so the recurring agent's
                // first tick is not waiting on a leadership election. Failover of the singular
                // agent belongs to the leadership-election compliance tier, not here.
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                // The tracking extension records only envelopes that land in the DURABLE inbox as
                // Scheduled — a buffered local queue keeps its scheduled occurrences in memory,
                // where nothing can verify or cancel them. Durable local queues are also what any
                // production host wanting durable recurring messages would run.
                opts.Policies.UseDurableLocalQueues();

                configurePersistence(opts);
                configure(opts);
            }).Build();

        // Tighten the loop BEFORE the host starts: the agent computes each sleep from these at
        // the END of a tick, so a knob tightened after startup only takes effect once the
        // already-running (up to 30s) sleep expires — which is exactly a test timeout.
        var agent = host.Services.GetServices<IAgentFamily>().OfType<RecurringMessageAgent>()
            .SingleOrDefault();
        if (agent != null)
        {
            agent.MaximumTickInterval = 1.Seconds();
            agent.VerificationInterval = 2.Seconds();
            _agents.Add(agent);
        }

        await host.StartAsync(TestContext.Current.CancellationToken);

        _hosts.Add(host);

        return host;
    }

    private async Task<RecurringMessageRecord> waitForTrackedPublishAsync(IMessageStore store, string name,
        TimeSpan? timeout = null)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout ?? 30.Seconds());

        while (DateTimeOffset.UtcNow < deadline)
        {
            var row = await store.RecurringMessages.LoadAsync(name, TestContext.Current.CancellationToken);
            if (row is { EnvelopeIds.Length: > 0 })
            {
                return row;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var agentDump = string.Join(" | ",
            _agents.Select(a => $"status={a.Status} published={a.OccurrencesPublished}"));
        var rows = await store.RecurringMessages.LoadAllAsync(TestContext.Current.CancellationToken);
        var rowDump = rows.Count == 0
            ? "none"
            : string.Join(" | ", rows.Select(r =>
                $"{r.Name}: ids={r.EnvelopeIds.Length} next={r.NextOccurrence:O} paused={r.Paused} cron='{r.CronExpression}' updated={r.LastUpdated:O}"));
        var counts = await store.Admin.FetchCountsAsync();
        throw new TimeoutException(
            $"No tracked publish for schedule '{name}' appeared in time. Agents: [{agentDump}]. " +
            $"Rows: [{rowDump}]. Counts: scheduled={counts.Scheduled} incoming={counts.Incoming}. " +
            $"Log problems: {string.Join(" || ", _logProblems)}");
    }

    [Fact]
    public async Task publishing_upserts_the_tracking_row_and_the_envelope_is_really_scheduled()
    {
        var host = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("hourly-compliance", "0 * * * *",
                _ => new RecurringComplianceMessage());
        });

        var store = host.Services.GetRequiredService<IMessageStore>();
        store.RecurringMessages.Enabled.ShouldBeTrue();

        var row = await waitForTrackedPublishAsync(store, "hourly-compliance");

        row.CronExpression.ShouldBe("0 * * * *");
        row.Paused.ShouldBeFalse();
        row.PausedAt.ShouldBeNull();

        // The pending occurrence is a real top-of-hour instant in the future...
        row.NextOccurrence.ShouldNotBeNull();
        row.NextOccurrence.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        row.NextOccurrence.Value.UtcDateTime.Minute.ShouldBe(0);

        // ...the row carries the occurrence's deterministic dedup id as its stable secondary key...
        row.DeduplicationId.ShouldBe($"hourly-compliance:{row.NextOccurrence.Value.ToUniversalTime():O}");

        // ...and the tracked envelope ids point at rows that really sit Scheduled in the inbox —
        // both by the extension's own verification count and by the ScheduledMessages surface
        // management tooling reads.
        (await store.RecurringMessages.CountStillScheduledAsync(row.EnvelopeIds,
            TestContext.Current.CancellationToken)).ShouldBe(row.EnvelopeIds.Length);

        var scheduled = await store.ScheduledMessages.QueryAsync(
            new ScheduledMessageQuery { MessageIds = row.EnvelopeIds }, TestContext.Current.CancellationToken);
        scheduled.Messages.Count.ShouldBe(row.EnvelopeIds.Length);
    }

    [Fact]
    public async Task verification_detects_a_cancelled_envelope_and_republishes_it()
    {
        var host = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("verified-compliance", "0 * * * *",
                _ => new RecurringComplianceMessage());
        });

        var store = host.Services.GetRequiredService<IMessageStore>();

        var original = await waitForTrackedPublishAsync(store, "verified-compliance");

        // Cancel the pre-scheduled occurrence out from under the schedule, exactly the way an
        // operator (or the console) would — through the ScheduledMessages surface on the
        // TRACKED id, which is also the fact that the tracked id is the real, cancellable one.
        await store.ScheduledMessages.CancelAsync(
            new ScheduledMessageQuery { MessageIds = original.EnvelopeIds },
            TestContext.Current.CancellationToken);

        (await store.RecurringMessages.CountStillScheduledAsync(original.EnvelopeIds,
            TestContext.Current.CancellationToken)).ShouldBe(0);

        // Record-and-verify: the agent notices the loss and re-publishes the SAME occurrence
        // under fresh envelope ids, re-recording them on the row.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        RecurringMessageRecord? republished = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var row = await store.RecurringMessages.LoadAsync("verified-compliance",
                TestContext.Current.CancellationToken);
            if (row is { EnvelopeIds.Length: > 0 } && !row.EnvelopeIds.Intersect(original.EnvelopeIds).Any())
            {
                republished = row;
                break;
            }

            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        republished.ShouldNotBeNull("the cancelled occurrence was never re-published");
        republished.NextOccurrence.ShouldBe(original.NextOccurrence);
        republished.DeduplicationId.ShouldBe(original.DeduplicationId);

        (await store.RecurringMessages.CountStillScheduledAsync(republished.EnvelopeIds,
            TestContext.Current.CancellationToken)).ShouldBe(republished.EnvelopeIds.Length);
    }

    [Fact]
    public async Task pause_marks_the_row_and_eagerly_cancels_the_pending_envelope()
    {
        var host = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("paused-compliance", "0 * * * *",
                _ => new RecurringComplianceMessage());
        });

        var store = host.Services.GetRequiredService<IMessageStore>();
        var control = host.Services.GetRequiredService<IRecurringScheduleControl>();

        var before = await waitForTrackedPublishAsync(store, "paused-compliance");

        await control.PauseAsync("paused-compliance", TestContext.Current.CancellationToken);

        // The cancel is EAGER — the pending envelope is gone the moment the pause returns, not
        // at the agent's next tick (the caller may be on a different node than the agent).
        (await store.RecurringMessages.CountStillScheduledAsync(before.EnvelopeIds,
            TestContext.Current.CancellationToken)).ShouldBe(0);

        var row = await store.RecurringMessages.LoadAsync("paused-compliance",
            TestContext.Current.CancellationToken);
        row.ShouldNotBeNull();
        row.Paused.ShouldBeTrue();
        row.PausedAt.ShouldNotBeNull();
        row.EnvelopeIds.ShouldBeEmpty();
        row.NextOccurrence.ShouldBeNull();

        // Double-pause is a no-op that keeps the original pause instant.
        var firstPausedAt = row.PausedAt;
        await control.PauseAsync("paused-compliance", TestContext.Current.CancellationToken);
        var again = await store.RecurringMessages.LoadAsync("paused-compliance",
            TestContext.Current.CancellationToken);
        again!.Paused.ShouldBeTrue();
        again.PausedAt.ShouldBe(firstPausedAt);
    }

    [Fact]
    public async Task pause_survives_restart_nothing_fires_while_paused_and_resume_is_strictly_after_now()
    {
        RecurringComplianceMessageHandler.Reset();

        // Phase 1: pause the schedule under an hourly cron, then stop the host — the pause has to
        // be durable state, not agent memory.
        var first = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("restartable-compliance", "0 * * * *",
                _ => new RecurringComplianceMessage());
        });

        var firstStore = first.Services.GetRequiredService<IMessageStore>();
        await waitForTrackedPublishAsync(firstStore, "restartable-compliance");
        await first.Services.GetRequiredService<IRecurringScheduleControl>()
            .PauseAsync("restartable-compliance", TestContext.Current.CancellationToken);
        var pausedAt = (await firstStore.RecurringMessages.LoadAsync("restartable-compliance",
            TestContext.Current.CancellationToken))!.PausedAt;
        await first.StopAsync(TestContext.Current.CancellationToken);

        // Phase 2: SAME schedule name, but now firing every ten seconds, against the same
        // database with no reset. Deterministic by construction: the durable pause was in place
        // before this host ever existed, so there is no publish/pause race to lose — if the agent
        // honours the row, nothing is ever even scheduled.
        var second = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("restartable-compliance",
                "*/10 * * * * *", _ => new RecurringComplianceMessage());
        }, clean: false);

        var store = second.Services.GetRequiredService<IMessageStore>();

        // Let several would-be occurrences pass.
        await Task.Delay(12.Seconds(), TestContext.Current.CancellationToken);

        RecurringComplianceMessageHandler.Received.ShouldBeEmpty();
        var row = await store.RecurringMessages.LoadAsync("restartable-compliance",
            TestContext.Current.CancellationToken);
        row!.Paused.ShouldBeTrue("the pause did not survive the restart");
        row.PausedAt.ShouldBe(pausedAt);
        row.EnvelopeIds.ShouldBeEmpty();

        // Phase 3: resume. The next occurrence is strictly after the resume instant — the paused
        // window is never back-filled — and the schedule actually fires end to end.
        var resumedAt = DateTimeOffset.UtcNow;
        await second.Services.GetRequiredService<IRecurringScheduleControl>()
            .ResumeAsync("restartable-compliance", TestContext.Current.CancellationToken);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (RecurringComplianceMessageHandler.Received.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var envelope = RecurringComplianceMessageHandler.Received.FirstOrDefault();
        envelope.ShouldNotBeNull("the resumed schedule never fired");

        // The occurrence instant rides in the deterministic dedup id; parse it back and prove
        // no part of the paused window was replayed.
        var prefix = "restartable-compliance:";
        envelope.DeduplicationId.ShouldNotBeNull();
        envelope.DeduplicationId.ShouldStartWith(prefix);
        var occurrence = DateTimeOffset.Parse(envelope.DeduplicationId![prefix.Length..]);
        occurrence.ShouldBeGreaterThan(resumedAt);
    }

    [Fact]
    public async Task a_restarted_host_adopts_the_predecessors_pending_occurrence_and_it_still_fires()
    {
        RecurringComplianceMessageHandler.Reset();

        // Phase 1: publish an occurrence on a 15s cadence, remember its envelope ids, stop.
        var first = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("adopted-compliance",
                "*/15 * * * * *", _ => new RecurringComplianceMessage());
        });

        var firstStore = first.Services.GetRequiredService<IMessageStore>();
        var tracked = await waitForTrackedPublishAsync(firstStore, "adopted-compliance");
        await first.StopAsync(TestContext.Current.CancellationToken);

        // Phase 2: a successor against the same database. The pre-scheduled occurrence survived
        // the restart in the durable inbox; the successor's agent must ADOPT it off the tracking
        // row rather than double-publish, and the occurrence must actually fire.
        var second = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("adopted-compliance",
                "*/15 * * * * *", _ => new RecurringComplianceMessage());
        }, clean: false);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        while (RecurringComplianceMessageHandler.Received.Count == 0 && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        var envelope = RecurringComplianceMessageHandler.Received.FirstOrDefault();
        envelope.ShouldNotBeNull("the predecessor's pre-scheduled occurrence never fired after the restart");

        // The envelope that fired is the PREDECESSOR's — restart durability and adoption in one
        // assertion. (The dedupe layer would collapse a double-publish at consumption anyway;
        // adoption means the duplicate is never even created.)
        tracked.EnvelopeIds.ShouldContain(envelope.Id);
    }

    [Fact]
    public async Task pausing_an_unknown_schedule_throws()
    {
        var host = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("known-compliance", "0 * * * *",
                _ => new RecurringComplianceMessage());
        });

        var control = host.Services.GetRequiredService<IRecurringScheduleControl>();

        var ex = await Should.ThrowAsync<UnknownRecurringScheduleException>(
            () => control.PauseAsync("no-such-schedule", TestContext.Current.CancellationToken));
        ex.Message.ShouldContain("no-such-schedule");
        ex.Message.ShouldContain("known-compliance");

        await Should.ThrowAsync<UnknownRecurringScheduleException>(
            () => control.ResumeAsync("no-such-schedule", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task the_opt_in_is_schema_neutral_for_hosts_without_schedules()
    {
        // A host that never registers a schedule must migrate EXACTLY as it did before the
        // feature existed: no recurring tracking table, no deduplication table (that flag rides
        // the same registration), and the null store on the extension point.
        var without = await buildHost(_ => { });

        var store = without.Services.GetRequiredService<IMessageStore>();
        store.RecurringMessages.Enabled.ShouldBeFalse();
        store.RecurringMessages.ShouldBeSameAs(NullRecurringMessageStore.Instance);

        // EndsWith rather than equality because SQLite folds the "schema" into a table-name
        // prefix (GH-3943), so the rendered identifier is prefixed on that provider.
        var objects = ((Weasel.Core.Migrations.IDatabase)store).AllObjects()
            .Select(x => x.Identifier.Name).ToArray();
        objects.Any(x => x.EndsWith(DatabaseConstants.RecurringMessagesTableName)).ShouldBeFalse();
        objects.Any(x => x.EndsWith(DatabaseConstants.DeduplicationTableName)).ShouldBeFalse();

        await without.StopAsync(TestContext.Current.CancellationToken);

        // And the registration IS the whole opt-in: the same persistence with one schedule
        // provisions both.
        var with = await buildHost(opts =>
        {
            opts.Schedules.ScheduleRecurring<RecurringComplianceMessage>("neutrality-compliance", "0 * * * *",
                _ => new RecurringComplianceMessage());
        });

        var optedIn = with.Services.GetRequiredService<IMessageStore>();
        optedIn.RecurringMessages.Enabled.ShouldBeTrue();

        var optedInObjects = ((Weasel.Core.Migrations.IDatabase)optedIn).AllObjects()
            .Select(x => x.Identifier.Name).ToArray();
        optedInObjects.Any(x => x.EndsWith(DatabaseConstants.RecurringMessagesTableName)).ShouldBeTrue();
        optedInObjects.Any(x => x.EndsWith(DatabaseConstants.DeduplicationTableName)).ShouldBeTrue();
    }
}

internal class ProblemCapturingLoggerProvider(System.Collections.Concurrent.ConcurrentQueue<string> problems)
    : Microsoft.Extensions.Logging.ILoggerProvider
{
    public Microsoft.Extensions.Logging.ILogger CreateLogger(string categoryName)
        => new ProblemLogger(categoryName, problems);

    public void Dispose()
    {
    }

    private class ProblemLogger(string category, System.Collections.Concurrent.ConcurrentQueue<string> problems)
        : Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel)
            => logLevel >= Microsoft.Extensions.Logging.LogLevel.Warning;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            problems.Enqueue($"[{logLevel}] {category}: {formatter(state, exception)} {exception?.Message}");
        }
    }
}

public class RecurringComplianceMessage;

public static class RecurringComplianceMessageHandler
{
    private static readonly List<Envelope> _received = [];
    private static readonly object _lock = new();

    public static IReadOnlyList<Envelope> Received
    {
        get
        {
            lock (_lock)
            {
                return _received.ToArray();
            }
        }
    }

    public static void Reset()
    {
        lock (_lock)
        {
            _received.Clear();
        }
    }

    public static void Handle(RecurringComplianceMessage message, Envelope envelope)
    {
        lock (_lock)
        {
            _received.Add(envelope);
        }
    }
}
