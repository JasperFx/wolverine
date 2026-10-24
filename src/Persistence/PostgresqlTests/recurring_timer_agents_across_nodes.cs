using System.Collections.Concurrent;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Runtime.Agents;
using Wolverine.Runtime.Recurring;
using Wolverine.Tracking;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// GH-5168. A short-cadence recurring schedule runs on a timer agent of its own, placed by agent
/// assignment. Against a real PostgreSQL message store: the schedule fires on time through the
/// agent, a two-node cluster runs the agent on exactly one node so no occurrence is handled twice,
/// the agent moves when its node goes away, and pausing from either node stops the agent through an
/// agent restriction and mirrors the pause onto the tracking row.
/// </summary>
public class recurring_timer_agents_across_nodes : IAsyncLifetime
{
    private const string SchemaName = "recurring_timers_5168";
    private const string ScheduleName = "every-five-seconds";

    private readonly List<IHost> _hosts = [];

    public async ValueTask InitializeAsync()
    {
        TimerOccurrenceHandler.Reset();

        // Agent restrictions and recurring rows are durable, so a pause left behind by an earlier
        // run would still be in force. Start from no schema at all.
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync();
        await conn.DropSchemaAsync(SchemaName);
        await conn.CloseAsync();
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts.ToArray().Reverse())
        {
            try
            {
                await host.StopAsync();
            }
            catch (Exception)
            {
                // Already stopped by the test
            }

            host.Dispose();
        }
    }

    private async Task<IHost> startNodeAsync(DurabilityMode mode = DurabilityMode.Balanced)
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(recurring_timer_agents_across_nodes).Assembly;
                opts.ServiceName = "recurring-timers";

                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, SchemaName);
                opts.Services.AddResourceSetupOnStartup();

                opts.Durability.Mode = mode;
                opts.Durability.HealthCheckPollingTime = 1.Seconds();
                opts.Durability.CheckAssignmentPeriod = 1.Seconds();

                #region sample_short_cadence_recurring_schedule

                // Every five seconds -- well under the 15 minute TimerThreshold, so this
                // schedule runs on a timer agent of its own and each occurrence is published
                // the moment it comes due
                opts.Schedules.ScheduleRecurring(ScheduleName, "*/5 * * * * *",
                    occurrence => new TimerOccurrence(occurrence));

                #endregion
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    private static Uri AgentUri => RecurringTimerAgentFamily.UriFor(ScheduleName);

    [Fact]
    public async Task the_schedule_fires_on_time_on_exactly_one_node()
    {
        var node1 = await startNodeAsync();
        var node2 = await startNodeAsync();

        node1.GetRuntime().Options.Schedules.FindByName(ScheduleName)!.ExecutionPath
            .ShouldBe(RecurringExecutionPath.Timer);

        var owner = await waitForOwnerAsync([node1, node2]);

        // Let several occurrences go by, then look at everything both nodes handled
        TimerOccurrenceHandler.Reset();
        await Task.Delay(17.Seconds(), TestContext.Current.CancellationToken);

        var handled = TimerOccurrenceHandler.Snapshot();
        handled.Length.ShouldBeGreaterThanOrEqualTo(3, describe(handled));

        // No occurrence was handled twice anywhere in the cluster...
        handled.GroupBy(x => x.Occurrence).ShouldAllBe(g => g.Count() == 1, describe(handled));

        // ...every one came from the node running the agent...
        handled.ShouldAllBe(x => x.ServiceNode == owner.GetRuntime().DurabilitySettings.AssignedNodeNumber,
            describe(handled));

        // ...and it fired on time: published the moment it came due, not when a poller next looked
        foreach (var occurrence in handled)
        {
            (occurrence.HandledAt - occurrence.Occurrence).ShouldBeLessThan(2.Seconds(), describe(handled));
        }
    }

    [Fact]
    public async Task the_agent_moves_when_its_node_goes_away()
    {
        var node1 = await startNodeAsync();
        var node2 = await startNodeAsync();

        var owner = await waitForOwnerAsync([node1, node2]);
        var survivor = owner == node1 ? node2 : node1;

        await owner.StopAsync(TestContext.Current.CancellationToken);
        _hosts.Remove(owner);
        owner.Dispose();

        await waitUntilAsync(() => survivor.GetRuntime().Agents.AllRunningAgentUris().Contains(AgentUri),
            60.Seconds(), "the surviving node never picked up the timer agent");

        TimerOccurrenceHandler.Reset();
        await waitUntilAsync(() => TimerOccurrenceHandler.Snapshot().Length >= 2, 30.Seconds(),
            "the schedule did not keep firing after its agent moved");
    }

    [Fact]
    public async Task pausing_stops_the_agent_cluster_wide_and_mirrors_onto_the_row()
    {
        var node1 = await startNodeAsync();
        var node2 = await startNodeAsync();

        var owner = await waitForOwnerAsync([node1, node2]);

        // Pause from the node that is NOT running the agent
        var other = owner == node1 ? node2 : node1;
        var control = other.Services.GetRequiredService<IRecurringScheduleControl>();

        await control.PauseAsync(ScheduleName, TestContext.Current.CancellationToken);

        // The restriction is applied and confirmed before PauseAsync returns
        owner.GetRuntime().Agents.AllRunningAgentUris().ShouldNotContain(AgentUri);

        var row = await other.GetRuntime().Storage.RecurringMessages.LoadAsync(ScheduleName, TestContext.Current.CancellationToken);
        row.ShouldNotBeNull().Paused.ShouldBeTrue();

        var descriptor = await describeAsync(other);
        descriptor.Paused.ShouldBeTrue();
        descriptor.ExecutionPath.ShouldBe(RecurringExecutionPath.Timer);
        descriptor.AgentUri.ShouldBe(AgentUri);

        await Should.ThrowAsync<RecurringSchedulePausedException>(() => control.TriggerAsync(ScheduleName, TestContext.Current.CancellationToken));

        TimerOccurrenceHandler.Reset();
        await Task.Delay(7.Seconds(), TestContext.Current.CancellationToken);
        TimerOccurrenceHandler.Snapshot().ShouldBeEmpty("a paused timer schedule still fired");

        // Resume from the other node, and the schedule fires again
        await owner.Services.GetRequiredService<IRecurringScheduleControl>().ResumeAsync(ScheduleName, TestContext.Current.CancellationToken);

        (await other.GetRuntime().Storage.RecurringMessages.LoadAsync(ScheduleName, TestContext.Current.CancellationToken))!.Paused.ShouldBeFalse();
        (await describeAsync(other)).Paused.ShouldBeFalse();

        await waitUntilAsync(() => TimerOccurrenceHandler.Snapshot().Length >= 1, 30.Seconds(),
            "the schedule did not fire again after the resume");
    }

    /// <summary>
    /// GH-5204. A timer schedule paused through its agent alone -- an agents screen, an operator's own
    /// ApplyRestrictionsAsync -- never touches the tracking table, so there is no row. QueryAsync used to
    /// report only rows, and so left the paused schedule out entirely.
    /// </summary>
    [Fact]
    public async Task a_timer_schedule_paused_through_its_agent_alone_is_reported_paused()
    {
        var node = await startNodeAsync();
        var runtime = node.GetRuntime();
        var control = node.Services.GetRequiredService<IRecurringScheduleControl>();

        var pause = new AgentRestrictions([]);
        pause.PauseAgent(AgentUri);
        await runtime.Agents.ApplyRestrictionsAsync(pause, TestContext.Current.CancellationToken);

        // The precondition the defect needs: nothing wrote a row for the timer schedule
        (await runtime.Storage.RecurringMessages.LoadAsync(ScheduleName, TestContext.Current.CancellationToken))
            .ShouldBeNull();

        var reported = (await control.QueryAsync(TestContext.Current.CancellationToken))
            .ShouldHaveSingleItem();
        reported.Name.ShouldBe(ScheduleName);
        reported.Paused.ShouldBeTrue();
        reported.CronExpression.ShouldBe("*/5 * * * * *");

        await Should.ThrowAsync<RecurringSchedulePausedException>(() =>
            control.TriggerAsync(ScheduleName, TestContext.Current.CancellationToken));

        // Lifted through the agent again, and it is no longer reported paused
        await runtime.Agents.ApplyRestrictionsAsync(
            new AgentRestrictions([new AgentRestriction(Guid.NewGuid(), AgentUri, AgentRestrictionType.None, 0)]),
            TestContext.Current.CancellationToken);

        (await control.QueryAsync(TestContext.Current.CancellationToken))
            .ShouldNotContain(x => x.Name == ScheduleName && x.Paused);
    }

    [Fact]
    public async Task solo_mode_runs_and_pauses_the_timer_agent_locally()
    {
        var host = await startNodeAsync(DurabilityMode.Solo);
        var runtime = host.GetRuntime();

        await waitUntilAsync(() => runtime.Agents.AllRunningAgentUris().Contains(AgentUri), 30.Seconds(),
            "the Solo node never started the timer agent");

        await waitUntilAsync(() => TimerOccurrenceHandler.Snapshot().Length >= 1, 30.Seconds(),
            "the schedule never fired in Solo mode");

        var control = host.Services.GetRequiredService<IRecurringScheduleControl>();
        await control.PauseAsync(ScheduleName, TestContext.Current.CancellationToken);

        runtime.Agents.AllRunningAgentUris().ShouldNotContain(AgentUri);
        (await runtime.Storage.RecurringMessages.LoadAsync(ScheduleName, TestContext.Current.CancellationToken))!.Paused.ShouldBeTrue();

        // Several Solo assignment periods: the paused agent must not be restarted
        TimerOccurrenceHandler.Reset();
        await Task.Delay(6.Seconds(), TestContext.Current.CancellationToken);
        runtime.Agents.AllRunningAgentUris().ShouldNotContain(AgentUri);
        TimerOccurrenceHandler.Snapshot().ShouldBeEmpty();

        await control.ResumeAsync(ScheduleName, TestContext.Current.CancellationToken);
        runtime.Agents.AllRunningAgentUris().ShouldContain(AgentUri);
    }

    private static async Task<RecurringScheduleDescriptor> describeAsync(IHost host)
    {
        var capabilities = await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, TestContext.Current.CancellationToken);
        return capabilities.RecurringSchedules.Single(x => x.Name == ScheduleName);
    }

    private static async Task<IHost> waitForOwnerAsync(IHost[] nodes)
    {
        IHost[] owners = [];
        await waitUntilAsync(() =>
        {
            owners = nodes.Where(x => x.GetRuntime().Agents.AllRunningAgentUris().Contains(AgentUri)).ToArray();
            return owners.Length == 1;
        }, 60.Seconds(), "the timer agent was never running on exactly one node");

        return owners.Single();
    }

    private static async Task waitUntilAsync(Func<bool> condition, TimeSpan timeout, string because)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100.Milliseconds());
        }

        throw new TimeoutException($"{because} (within {timeout})");
    }

    private static string describe(IEnumerable<HandledOccurrence> handled)
    {
        return string.Join(", ",
            handled.Select(x => $"{x.Occurrence:HH:mm:ss.fff} on node {x.ServiceNode} at {x.HandledAt:HH:mm:ss.fff}"));
    }
}

public record TimerOccurrence(DateTimeOffset Occurrence);

public record HandledOccurrence(DateTimeOffset Occurrence, int ServiceNode, DateTimeOffset HandledAt);

public static class TimerOccurrenceHandler
{
    private static ConcurrentQueue<HandledOccurrence> _handled = new();

    public static void Reset() => _handled = new ConcurrentQueue<HandledOccurrence>();

    public static HandledOccurrence[] Snapshot() => _handled.ToArray();

    public static void Handle(TimerOccurrence message, IWolverineRuntime runtime)
    {
        _handled.Enqueue(new HandledOccurrence(message.Occurrence,
            runtime.DurabilitySettings.AssignedNodeNumber, DateTimeOffset.UtcNow));
    }
}
