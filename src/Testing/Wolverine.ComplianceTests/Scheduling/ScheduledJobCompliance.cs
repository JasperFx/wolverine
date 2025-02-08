using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.ComplianceTests.Scheduling;

public class ScheduledMessageReceiver
{
    public readonly IList<ScheduledMessage> ReceivedMessages = new List<ScheduledMessage>();

    public readonly TaskCompletionSource<ScheduledMessage> Source = new();

    public Task<ScheduledMessage> Received => Source.Task;
}

public abstract class ScheduledJobCompliance: IAsyncLifetime
{
    private readonly ScheduledMessageReceiver theReceiver = new();
    private IHost theHost;
    
    public abstract void ConfigurePersistence(WolverineOptions opts);
    
    public async Task InitializeAsync()
    {
        theHost = await Host
            .CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.ScheduledJobPollingTime = 1.Seconds();

                opts.Services.AddSingleton(theReceiver);

                opts.Publish(x => x.MessagesFromAssemblyContaining<ScheduledMessageReceiver>()
                    .ToLocalQueue("incoming").UseDurableInbox());

                opts.Discovery.DisableConventionalDiscovery().IncludeType<ScheduledMessageCatcher>();

                ConfigurePersistence(opts);

            })
            .StartAsync();

        await theHost.ResetResourceState();
    }

    public async Task DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }
    
    protected ValueTask ScheduleMessage(int id, int seconds)
    {
        return theHost.Services.GetRequiredService<IMessageContext>()
            .ScheduleAsync(new ScheduledMessage { Id = id }, seconds.Seconds());
    }

    protected ValueTask ScheduleSendMessage(int id, int seconds)
    {
        return new Wolverine.Runtime.MessageBus(theHost.GetRuntime())
            .ScheduleAsync(new ScheduledMessage { Id = id }, seconds.Seconds());
    }

    protected int ReceivedMessageCount()
    {
        return theReceiver.ReceivedMessages.Count;
    }

    protected Task AfterReceivingMessages()
    {
        return theReceiver.Received;
    }

    protected int TheIdOfTheOnlyReceivedMessageShouldBe()
    {
        return theReceiver.ReceivedMessages.Single().Id;
    }

    protected async Task<int> PersistedScheduledCount()
    {
        var counts = await theHost.Services.GetRequiredService<IMessageStore>().Admin.FetchCountsAsync();
        return counts.Scheduled;
    }

    protected async Task PersistedScheduledCountShouldBe(int expected)
    {
        var stopwatch = new Stopwatch();
        stopwatch.Start();

        var count = await PersistedScheduledCount();
        while (stopwatch.Elapsed < 5.Seconds() && count != expected)
        {
            await Task.Delay(100.Milliseconds());
            count = await PersistedScheduledCount();
        }

        count.ShouldBe(expected);
    }

    [Fact]
    public async Task execute_scheduled_job()
    {
        await ScheduleSendMessage(1, 7200);
        await ScheduleSendMessage(2, 5);
        await ScheduleSendMessage(3, 7200);

        ReceivedMessageCount().ShouldBe(0);

        await AfterReceivingMessages();


        //TheIdOfTheOnlyReceivedMessageShouldBe().ShouldBe(2);

        while (await PersistedScheduledCount() != 2)
        {
            await Task.Delay(250.Milliseconds());
        }

        (await PersistedScheduledCount()).ShouldBe(2);
    }
}

public class ScheduledMessageCatcher
{
    private readonly ScheduledMessageReceiver _receiver;

    public ScheduledMessageCatcher(ScheduledMessageReceiver receiver)
    {
        _receiver = receiver;
    }

    public void Consume(ScheduledMessage message)
    {
        if (!_receiver.Source.Task.IsCompleted)
        {
            _receiver.Source.SetResult(message);
        }

        _receiver.ReceivedMessages.Add(message);
    }
}