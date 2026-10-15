using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

public class ScheduledMessageCancellationEndToEnd : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await AzureServiceBusTesting.DeleteAllEmulatorObjectsAsync();
    }

    [Fact]
    public async Task PublicApiCanRequestCancellationForAnAzureSchedule()
    {
        const string queueName = "scheduled-cancellation-e2e";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<ScheduledCancellationEndToEndMessage>()
                    .ToAzureServiceBusQueue(queueName)
                    .SendInline();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var scheduled = await host.MessageBus().ScheduleWithCancellationAsync(
            new ScheduledCancellationEndToEndMessage(), TimeSpan.FromMinutes(5));

        var result = (await scheduled.CancelAsync(TestContext.Current.CancellationToken)).Single();
        result.Status.ShouldBe(ScheduledMessageCancellationStatus.CancellationAccepted);
    }
}

public record ScheduledCancellationEndToEndMessage;
