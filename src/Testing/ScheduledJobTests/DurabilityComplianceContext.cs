using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Baseline;
using Baseline.Dates;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Lamar;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Oakton.Resources;
using Shouldly;
using TestingSupport;
using Wolverine.Runtime;
using Xunit;

namespace ScheduledJobTests;

public abstract class DurabilityComplianceContext<TTriggerHandler, TItemCreatedHandler> : IAsyncLifetime
{
    private IHost theReceiver;
    private IHost theSender;

    protected DurabilityComplianceContext()
    {
        WolverineOptions.RememberedApplicationAssembly = GetType().Assembly;
    }

    public async Task InitializeAsync()
    {
        var receiverPort = PortFinder.GetAvailablePort();
        var senderPort = PortFinder.GetAvailablePort();


        var senderRegistry = new WolverineOptions();
        senderRegistry.Services.ForSingletonOf<ILogger>().Use(NullLogger.Instance);
        senderRegistry.Handlers
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

        theSender = WolverineHost.For(senderRegistry);


        var receiverRegistry = new WolverineOptions();
        receiverRegistry.Services.ForSingletonOf<ILogger>().Use(NullLogger.Instance);
        receiverRegistry.Handlers.DisableConventionalDiscovery()
            .IncludeType<TTriggerHandler>()
            .IncludeType<TItemCreatedHandler>()
            .IncludeType<QuestionHandler>()
            .IncludeType<ScheduledMessageHandler>();

        receiverRegistry.ListenAtPort(receiverPort).UseDurableInbox();

        configureReceiver(receiverRegistry);


        theReceiver = WolverineHost.For(receiverRegistry);

        await theSender.ResetResourceState();
        await theReceiver.ResetResourceState();

        ScheduledMessageHandler.Reset();

        await buildAdditionalObjects();
    }

    protected virtual Task buildAdditionalObjects() => Task.CompletedTask;

    protected abstract void configureReceiver(WolverineOptions receiverRegistry);

    protected abstract void configureSender(WolverineOptions senderRegistry);


    public async Task DisposeAsync()
    {
        if (theReceiver != null)
        {
            await theReceiver.StopAsync();
        }

        if (theSender != null)
        {
            await theSender.StopAsync();
        }
    }

    [Fact]
    public async Task<bool> CanSendMessageEndToEnd()
    {
        await cleanDatabase();

        var trigger = new TriggerMessage { Name = Guid.NewGuid().ToString() };

        await theSender
            .TrackActivity()
            .AlsoTrack(theReceiver)
            .WaitForMessageToBeReceivedAt<CascadedMessage>(theSender)
            .SendMessageAndWaitAsync(trigger);

        return true;
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
        var container = theSender.Services.As<IContainer>();
        await using var nested = container.GetNestedContainer();
        await withContext(theSender, nested.GetInstance<IMessageContext>().As<MessageContext>(), action);
    }

    [Fact]
    public async Task<bool> CanSendItemDurably()
    {
        await cleanDatabase();


        var item = new ItemCreated
        {
            Name = "Shoe",
            Id = Guid.NewGuid()
        };

        await theSender.TrackActivity().AlsoTrack(theReceiver).SendMessageAndWaitAsync(item);

        await Task.Delay(500.Milliseconds());

        await assertReceivedItemMatchesSent(item);

        await assertIncomingEnvelopesIsZero();

        var senderCounts = await assertNoPersistedOutgoingEnvelopes();

        senderCounts.Outgoing.ShouldBe(0);

        return true;
    }

    private async Task<PersistedCounts> assertNoPersistedOutgoingEnvelopes()
    {
        var senderCounts = await theSender.Get<IMessageStore>().Admin.FetchCountsAsync();
        if (senderCounts.Outgoing > 0)
        {
            await Task.Delay(500.Milliseconds());
            senderCounts = await theSender.Get<IMessageStore>().Admin.FetchCountsAsync();
        }

        return senderCounts;
    }

    private async Task assertReceivedItemMatchesSent(ItemCreated item)
    {
        var received = loadItem(theReceiver, item.Id);
        if (received == null)
        {
            await Task.Delay(500.Milliseconds());
        }

        received = loadItem(theReceiver, item.Id);

        received.Name.ShouldBe(item.Name);

    }

    private async Task assertIncomingEnvelopesIsZero()
    {
        var receiverCounts = await theReceiver.Get<IMessageStore>().Admin.FetchCountsAsync();
        if (receiverCounts.Incoming > 0)
        {
            await Task.Delay(500.Milliseconds());
            receiverCounts = await theReceiver.Get<IMessageStore>().Admin.FetchCountsAsync();
        }

        receiverCounts.Incoming.ShouldBe(0);
    }

    [Fact]
    public async Task<bool> CanScheduleJobDurably()
    {
        await cleanDatabase();

        var item = new ItemCreated
        {
            Name = "Shoe",
            Id = Guid.NewGuid()
        };

        await send(async c =>
        {
            await c.ScheduleAsync(item, 1.Hours());
        });

        var persistence = theSender.Get<IMessageStore>();
        var counts = await persistence.Admin.FetchCountsAsync();
        counts.Scheduled.ShouldBe(1);

        return true;
    }


    [Fact]
    public async Task<bool> SendWithReceiverDown()
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

        return true;
    }

    protected abstract IReadOnlyList<Envelope> loadAllOutgoingEnvelopes(IHost sender);


    [Fact]
    public async Task<bool> SendScheduledMessage()
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

        return true;
    }

    [Fact]
    public async Task<bool> ScheduleJobLocally()
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

        return true;
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
