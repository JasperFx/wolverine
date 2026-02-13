using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class using_native_scheduling : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

    [Fact]
    public async Task with_inline_endpoint()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("inline1").ProcessInline();
                opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("inline1");
            }).StartAsync();

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(20.Seconds())
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new AsbMessage1("later"), 3.Seconds()));

        session.Received.SingleMessage<AsbMessage1>()
            .Name.ShouldBe("later");

        await host.StopAsync();
    }

    [Fact]
    public async Task with_inline_endpoint_cascaded_timeout()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("inline1").ProcessInline();
                opts.PublishAllMessages().ToAzureServiceBusQueue("inline1");
            }).StartAsync();

        var referenceTime = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromSeconds(1);
        var margin = TimeSpan.FromSeconds(2);

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<AsbCascadedTimeout>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new AsbTriggerCascadedTimeout(delay)));

        var envelope = session.Scheduled.Envelopes().Single(e => e.Message is AsbCascadedTimeout);
        envelope.ShouldNotBeNull();
        envelope.ScheduledTime!.Value.ShouldBeInRange(referenceTime.Add(delay - margin), referenceTime.Add(delay + margin));

        await host.StopAsync();
    }

    [Fact]
    public async Task with_inline_endpoint_explicit_scheduling()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("inline1").ProcessInline();
                opts.PublishAllMessages().ToAzureServiceBusQueue("inline1");
            }).StartAsync();

        var referenceTime = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromSeconds(1);
        var margin = TimeSpan.FromSeconds(2);

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<AsbExplicitScheduled>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new AsbTriggerExplicitScheduled(delay)));

        var envelope = session.Scheduled.Envelopes().Single(e => e.Message is AsbExplicitScheduled);
        envelope.ShouldNotBeNull();
        envelope.ScheduledTime!.Value.ShouldBeInRange(referenceTime.Add(delay - margin), referenceTime.Add(delay + margin));

        await host.StopAsync();
    }

    [Fact]
    public async Task with_buffered_endpoint() // durable would have similar mechanics
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("buffered1").BufferedInMemory();
                opts.PublishMessage<AsbMessage1>().ToAzureServiceBusQueue("buffered1");
            }).StartAsync();

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(20.Seconds())
            .ExecuteAndWaitAsync(c => c.ScheduleAsync(new AsbMessage1("in a bit"), 3.Seconds()));

        session.Received.SingleMessage<AsbMessage1>()
            .Name.ShouldBe("in a bit");

        await host.StopAsync();
    }
}

public record AsbTriggerCascadedTimeout(TimeSpan Delay);
public record AsbCascadedTimeout(string Id, TimeSpan delay) : TimeoutMessage(delay);

public record AsbTriggerExplicitScheduled(TimeSpan Delay);
public record AsbExplicitScheduled(string Id);

public class AsbScheduledMessageHandler
{
    public AsbCascadedTimeout Handle(AsbTriggerCascadedTimeout trigger)
    {
        return new AsbCascadedTimeout("test-timeout", trigger.Delay);
    }
    public static void Handle(AsbCascadedTimeout timeout)
    {
    }

    public async Task Handle(AsbTriggerExplicitScheduled trigger, IMessageContext context)
    {
        await context.ScheduleAsync(new AsbExplicitScheduled("test"), trigger.Delay);
    }

    public static void Handle(AsbExplicitScheduled scheduled)
    {
    }
}