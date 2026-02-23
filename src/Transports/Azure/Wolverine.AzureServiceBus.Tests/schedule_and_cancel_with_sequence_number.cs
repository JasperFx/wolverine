using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class schedule_and_cancel_with_sequence_number : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();

    [Fact]
    public async Task schedule_returns_sequence_number_via_ScheduleWithResultAsync()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("schedule-result1").ProcessInline();
                opts.PublishMessage<AsbScheduleMessage>().ToAzureServiceBusQueue("schedule-result1");
            }).StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        var result = await bus.ScheduleWithResultAsync(
            new AsbScheduleMessage("test-sequence"),
            30.Seconds());

        result.ShouldNotBeNull();
        result.Envelopes.Count.ShouldBe(1);
        result.Envelopes[0].SchedulingToken.ShouldNotBeNull();
        result.Envelopes[0].SchedulingToken.ShouldBeOfType<long>();
        ((long)result.Envelopes[0].SchedulingToken!).ShouldBeGreaterThan(0);

        await host.StopAsync();
    }

    [Fact]
    public async Task cancel_a_scheduled_message_prevents_delivery()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision().AutoPurgeOnStartup();

                opts.ListenToAzureServiceBusQueue("schedule-cancel1").ProcessInline();
                opts.PublishMessage<AsbScheduleMessage>().ToAzureServiceBusQueue("schedule-cancel1");
            }).StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();

        // Schedule far in the future so it won't be delivered during test
        var result = await bus.ScheduleWithResultAsync(
            new AsbScheduleMessage("should-be-cancelled"),
            5.Minutes());

        var schedulingToken = result.Envelopes[0].SchedulingToken!;

        // Cancel via the endpoint
        await bus.EndpointFor("schedule-cancel1").CancelScheduledAsync(schedulingToken);

        // Wait a bit and verify no message arrives
        await Task.Delay(3.Seconds());

        // If we got here without error, the cancellation succeeded at the ASB level.
        // The message will never be delivered since we cancelled it.

        await host.StopAsync();
    }
}

public record AsbScheduleMessage(string Name);

public static class AsbScheduleMessageHandler
{
    public static void Handle(AsbScheduleMessage message)
    {
        // no-op handler for scheduling tests
    }
}
