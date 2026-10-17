using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
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

    [Fact]
    public async Task TransactionalCancellableScheduleUsesTheOutgoingOutbox()
    {
        const string queueName = "scheduled-cancellation-transactional";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<ScheduledCancellationEndToEndMessage>()
                    .ToAzureServiceBusQueue(queueName)
                    .UseDurableOutbox();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var transaction = Substitute.For<IEnvelopeTransaction>();
        var bus = new MessageBus(runtime);
        await bus.EnlistInOutboxAsync(transaction);
        var scheduledTime = DateTimeOffset.UtcNow.AddMinutes(5);

        var handle = await bus.ScheduleWithCancellationAsync(
            new ScheduledCancellationEndToEndMessage(), scheduledTime);

        await transaction.Received(1).PersistOutgoingAsync(Arg.Is<Envelope>(envelope =>
            envelope.Status == EnvelopeStatus.Outgoing &&
            envelope.OwnerId == runtime.Options.Durability.AssignedNodeNumber &&
            envelope.ScheduledTime == scheduledTime));
        await transaction.DidNotReceive().PersistIncomingAsync(Arg.Any<Envelope>());
        (await handle.CancelAsync(TestContext.Current.CancellationToken)).Single().Status
            .ShouldBe(ScheduledMessageCancellationStatus.NotScheduledYet);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    public async Task TransactionalAlreadyDueCancellableScheduleUsesTheOutgoingOutbox(int delayMilliseconds)
    {
        var queueName = $"scheduled-cancellation-transactional-already-due-{delayMilliseconds}";
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAzureServiceBusTesting()
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.PublishMessage<ScheduledCancellationEndToEndMessage>()
                    .ToAzureServiceBusQueue(queueName)
                    .UseDurableOutbox();
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var transaction = Substitute.For<IEnvelopeTransaction>();
        var bus = new MessageBus(runtime);
        await bus.EnlistInOutboxAsync(transaction);

        var handle = await bus.ScheduleWithCancellationAsync(
            new ScheduledCancellationEndToEndMessage(), TimeSpan.FromMilliseconds(delayMilliseconds));

        await transaction.Received(1).PersistOutgoingAsync(Arg.Is<Envelope>(envelope =>
            envelope.Status == EnvelopeStatus.Outgoing &&
            envelope.OwnerId == runtime.Options.Durability.AssignedNodeNumber &&
            envelope.ScheduledTime.HasValue && envelope.ScheduledTime.Value <= DateTimeOffset.UtcNow));
        await transaction.DidNotReceive().PersistIncomingAsync(Arg.Any<Envelope>());
        (await handle.CancelAsync(TestContext.Current.CancellationToken)).Single().Status
            .ShouldBe(ScheduledMessageCancellationStatus.NotScheduled);
    }
}

public record ScheduledCancellationEndToEndMessage;
