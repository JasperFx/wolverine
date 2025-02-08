using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using JasperFx.Resources;
using Shouldly;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests.Scheduling;

public abstract class DurabilityComplianceContext<TTriggerHandler, TItemCreatedHandler> : IAsyncLifetime
{
    private IHost theReceiver;
    private IHost theSender;

    public async Task InitializeAsync()
    {
        var receiverPort = PortFinder.GetAvailablePort();
        var senderPort = PortFinder.GetAvailablePort();
        
        theSender = WolverineHost.For(senderRegistry =>
        {
            senderRegistry.Durability.Mode = DurabilityMode.Solo;
            senderRegistry.Durability.ScheduledJobFirstExecution = 0.Seconds(); // Start immediately!
            senderRegistry.Durability.ScheduledJobPollingTime = 1.Seconds();
            senderRegistry.Services.AddSingleton<ILogger>(NullLogger.Instance);
            senderRegistry
                .DisableConventionalDiscovery()
                .IncludeType<CascadeReceiver>()
                .IncludeType<ScheduledMessageHandler>();

            senderRegistry.Publish(x =>
            {
                x.Message<TriggerMessage>();
                x.Message<ItemCreated>();
                x.Message<Question>();
                x.Message<ScheduledMessage>();

                x.ToPort(receiverPort).UseDurableOutbox();
            });
            
            senderRegistry.ListenAtPort(senderPort).UseDurableInbox();

            configureSender(senderRegistry);
        });

        await theSender.ClearAllPersistedWolverineDataAsync();

        theReceiver = WolverineHost.For(receiverRegistry =>
        {
            receiverRegistry.Durability.Mode = DurabilityMode.Solo;
            receiverRegistry.Services.AddSingleton<ILogger>(NullLogger.Instance);
            receiverRegistry.DisableConventionalDiscovery()
                .IncludeType<TTriggerHandler>()
                .IncludeType<TItemCreatedHandler>()
                .IncludeType<QuestionHandler>()
                .IncludeType<ScheduledMessageHandler>();

            receiverRegistry.ListenAtPort(receiverPort).UseDurableInbox();

            configureReceiver(receiverRegistry);
        });

        await theSender.ResetResourceState();
        await theReceiver.ResetResourceState();

        ScheduledMessageHandler.Reset();

        await buildAdditionalObjects();
    }

    public async Task DisposeAsync()
    {
        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
            theReceiver.Dispose();
        }

        if (theSender != null)
        {
            await theSender.StopAsync();
            theSender.Dispose();
        }
    }

    protected virtual Task buildAdditionalObjects()
    {
        return Task.CompletedTask;
    }

    protected abstract void configureReceiver(WolverineOptions receiverRegistry);

    protected abstract void configureSender(WolverineOptions senderRegistry);

    [Fact]
    public async Task CanSendMessageEndToEnd()
    {
        await cleanDatabase();

        var trigger = new TriggerMessage { Name = Guid.NewGuid().ToString() };

        await theSender
            .TrackActivity()
            .AlsoTrack(theReceiver)
            .WaitForMessageToBeReceivedAt<CascadedMessage>(theSender)
            .SendMessageAndWaitAsync(trigger);
    }

    private async ValueTask cleanDatabase()
    {
        await theReceiver.ResetResourceState();
        await theSender.ResetResourceState();
    }

    protected abstract ItemCreated loadItem(IHost receiver, Guid id);


    protected abstract Task withContext(IHost sender, MessageContext context,
        Func<MessageContext, ValueTask> action);

    private async Task send(Func<IMessageContext, ValueTask> action)
    {
        var context = new MessageContext(theSender.GetRuntime());
        
        await withContext(theSender, context, action);
    }

    [Fact]
    public async Task CanScheduleJobDurably()
    {
        await cleanDatabase();

        var item = new ItemCreated
        {
            Name = "Shoe",
            Id = Guid.NewGuid()
        };

        await send(async c => { await 
            c.ScheduleAsync(item, 1.Hours()); });

        var persistence = theSender.Get<IMessageStore>();
        var counts = await persistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(1);
    }

    [Fact]
    public async Task SendWithReceiverDown()
    {
        await cleanDatabase();

        // Shutting it down
        theReceiver.Dispose();
        theReceiver = null;


        var item = new ItemCreated
        {
            Name = "Shoe",
            Id = Guid.NewGuid()
        };

        await send(c => c.SendAsync(item));

        var outgoing = loadAllOutgoingEnvelopes(theSender).SingleOrDefault();

        outgoing.ShouldNotBeNull();
        outgoing.MessageType.ShouldBe(typeof(ItemCreated).ToMessageTypeName());
    }

    protected abstract IReadOnlyList<Envelope> loadAllOutgoingEnvelopes(IHost sender);


    [Fact]
    public async Task SendScheduledMessage()
    {
        await cleanDatabase();

        var message1 = new ScheduledMessage { Id = 1 };
        var message2 = new ScheduledMessage { Id = 22 };
        var message3 = new ScheduledMessage { Id = 3 };

        await send(async c =>
        {
            await c.ScheduleAsync(message1, 2.Hours());
            await c.ScheduleAsync(message2, 5.Seconds());
            await c.ScheduleAsync(message3, 2.Hours());
        });

        ScheduledMessageHandler.ReceivedMessages.Count.ShouldBe(0);

        await ScheduledMessageHandler.Received;

        ScheduledMessageHandler.ReceivedMessages.Single()
            .Id.ShouldBe(22);

    }

    [Fact]
    public async Task ScheduleJobLocally()
    {
        await cleanDatabase();

        var message1 = new ScheduledMessage { Id = 1 };
        var message2 = new ScheduledMessage { Id = 2 };
        var message3 = new ScheduledMessage { Id = 3 };


        await send(async c =>
        {
            await c.ScheduleAsync(message1, 2.Hours());
            await c.ScheduleAsync(message2, 5.Seconds());
            await c.ScheduleAsync(message3, 2.Hours());
        });


        ScheduledMessageHandler.ReceivedMessages.Count.ShouldBe(0);

        await ScheduledMessageHandler.Received;

        ScheduledMessageHandler.ReceivedMessages.Single()
            .Id.ShouldBe(2);

    }
}

public class ItemCreated
{
    public Guid Id;
    public string Name;
}

public class ScheduledMessage
{
    public int Id { get; set; }
}

public class ScheduledMessageHandler
{
    public static readonly IList<ScheduledMessage> ReceivedMessages = new List<ScheduledMessage>();

    private static TaskCompletionSource<ScheduledMessage> _source;

    public static Task<ScheduledMessage> Received { get; private set; }

    public void Consume(ScheduledMessage message)
    {
        ReceivedMessages.Add(message);
        _source?.TrySetResult(message);
    }

    public static void Reset()
    {
        _source = new TaskCompletionSource<ScheduledMessage>();
        Received = _source.Task;
        ReceivedMessages.Clear();
    }
}

public class CascadedMessage
{
    public string Name { get; set; }
}

public class CascadeReceiver
{
    public void Handle(CascadedMessage message)
    {
    }
}

public class TriggerMessage
{
    public string Name { get; set; }
}

public class QuestionHandler
{
    public Answer Handle(Question question)
    {
        return new Answer
        {
            Sum = question.X + question.Y,
            Product = question.X * question.Y
        };
    }
}

public class Question
{
    public int X;
    public int Y;
}

public class Answer
{
    public int Product;
    public int Sum;
}