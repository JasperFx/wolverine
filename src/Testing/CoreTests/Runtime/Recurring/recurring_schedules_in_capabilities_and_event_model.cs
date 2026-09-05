using JasperFx.Events.EventModeling;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration.Capabilities;
using Wolverine.Configuration.EventModeling;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime.Recurring;

/// <summary>
/// The diagnostics half of the recurring-message feature: registered schedules surface as a
/// first-class ServiceCapabilities section (typed fields, so a monitoring console never parses
/// deduplication-id strings, and a schedule change participates in capability change detection),
/// and a cron-triggered slice on the Event Model is triggered by the job scheduler with the cron
/// expression as its origin label — populated from the registration, never a parallel surface.
/// </summary>
public class recurring_schedules_in_capabilities_and_event_model : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // See recurring_messages_on_a_storeless_host for why the assembly pin matters (GH-3521).
                opts.ApplicationAssembly = typeof(recurring_schedules_in_capabilities_and_event_model).Assembly;
                opts.ServiceName = "recurring-capabilities";

                opts.Schedules.ScheduleRecurring<PendingRecurringMessage>("0 9 * * *");
                opts.Schedules.ScheduleRecurring("nightly", "0 2 * * *",
                    _ => new NightlyCapabilityMessage(), TimeZoneInfo.Utc);
            }).StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task the_capabilities_snapshot_carries_the_recurring_schedules_section()
    {
        var capabilities = await ServiceCapabilities.ReadFrom(_host.GetRuntime(), null,
            TestContext.Current.CancellationToken);

        capabilities.RecurringSchedules.Count.ShouldBe(2);

        // Ordered by name (ordinal), so the snapshot is stable across emits
        var nightly = capabilities.RecurringSchedules.Single(x => x.Name == "nightly");
        nightly.Name.ShouldBe("nightly");
        nightly.CronExpression.ShouldBe("0 2 * * *");
        nightly.TimeZoneId.ShouldBe(TimeZoneInfo.Utc.Id);
        nightly.MessageType.Name.ShouldBe(nameof(NightlyCapabilityMessage));
        nightly.Paused.ShouldBeFalse();
        nightly.PausedAt.ShouldBeNull();

        // On a storeless host there is no tracking row, so the pending occurrence falls back to
        // the computed next firing instant — a real 02:00 UTC in the future.
        nightly.NextOccurrence.ShouldNotBeNull();
        nightly.NextOccurrence.Value.ShouldBeGreaterThan(DateTimeOffset.UtcNow);
        nightly.NextOccurrence.Value.UtcDateTime.Hour.ShouldBe(2);

        var daily = capabilities.RecurringSchedules.Single(x => x.Name == nameof(PendingRecurringMessage));
        daily.CronExpression.ShouldBe("0 9 * * *");
        daily.MessageType.FullName.ShouldBe(typeof(PendingRecurringMessage).FullName);
    }

    [Fact]
    public async Task cron_scheduled_slices_are_job_scheduler_triggered_with_the_cron_as_origin_label()
    {
        var model = await WolverineEventModelExport.AssembleAsync(_host.Services,
            token: TestContext.Current.CancellationToken);

        var slice = model!.Slices.Single(x => x.Name == nameof(NightlyCapabilityMessage));
        slice.TriggerKind.ShouldBe(TriggerKind.JobScheduler);
        slice.TriggerOrigin.ShouldNotBeNull();
        slice.TriggerOrigin.Label.ShouldBe("0 2 * * *");
    }

    [Fact]
    public async Task a_host_without_schedules_reports_an_empty_section_and_plain_triggers()
    {
        using var plain = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ApplicationAssembly = typeof(recurring_schedules_in_capabilities_and_event_model).Assembly;
            }).StartAsync(TestContext.Current.CancellationToken);

        var capabilities = await ServiceCapabilities.ReadFrom(plain.GetRuntime(), null,
            TestContext.Current.CancellationToken);
        capabilities.RecurringSchedules.ShouldBeEmpty();

        var model = await WolverineEventModelExport.AssembleAsync(plain.Services,
            token: TestContext.Current.CancellationToken);
        model!.Slices.Single(x => x.Name == nameof(NightlyCapabilityMessage))
            .TriggerKind.ShouldBe(TriggerKind.MessageHandler);
    }
}

public class NightlyCapabilityMessage;

public static class NightlyCapabilityMessageHandler
{
    public static void Handle(NightlyCapabilityMessage message)
    {
    }
}
