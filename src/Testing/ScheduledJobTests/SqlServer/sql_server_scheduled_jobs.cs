using System.Diagnostics;
using Baseline.Dates;
using IntegrationTests;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.SqlServer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using ScheduledJobTests.Postgresql;
using Shouldly;

namespace ScheduledJobTests.SqlServer;

public class sql_server_scheduled_jobs : IAsyncLifetime
{
    private IHost theHost;
    private readonly ScheduledMessageReceiver theReceiver = new();

    public async Task InitializeAsync()
    {
        theHost = await Host
            .CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Advanced.ScheduledJobPollingTime = 50.Milliseconds();

                opts.Services.AddSingleton(theReceiver);

                opts.Publish(x => x.MessagesFromAssemblyContaining<ScheduledMessageReceiver>()
                    .ToLocalQueue("incoming").UseDurableInbox());

                opts.Handlers.Discovery(x =>
                {
                    x.DisableConventionalDiscovery();
                    x.IncludeType<ScheduledMessageCatcher>();
                });

                opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
            })
            .StartAsync();

        await theHost.ResetResourceState();
    }

    public Task DisposeAsync()
    {
        return theHost.StopAsync();
    }

    protected Task ScheduleMessage(int id, int seconds)
    {
        return theHost.Services.GetRequiredService<IMessageContext>()
            .ScheduleAsync(new ScheduledMessage { Id = id }, seconds.Seconds());
    }

    protected Task ScheduleSendMessage(int id, int seconds)
    {
        return theHost.Services.GetRequiredService<IMessageContext>()
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

        TheIdOfTheOnlyReceivedMessageShouldBe().ShouldBe(2);

        while (await PersistedScheduledCount() != 2)
        {
            await Task.Delay(100.Milliseconds());
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
