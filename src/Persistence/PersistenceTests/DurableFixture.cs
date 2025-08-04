using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using JasperFx.Resources;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Wolverine.Transports.Tcp;
using Wolverine.Util;
using Xunit;

namespace PersistenceTests;

public abstract class DurableFixture<TTriggerHandler, TItemCreatedHandler> : IAsyncLifetime
{
    private IHost theReceiver;
    private IHost theSender;

    public async Task InitializeAsync()
    {
        var receiverPort = PortFinder.GetAvailablePort();
        var senderPort = PortFinder.GetAvailablePort();
        
        theSender = WolverineHost.For(senderRegistry =>
        {
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
        
        await theSender.ResetResourceState();

        theReceiver = WolverineHost.For(receiverRegistry =>
        {
            receiverRegistry.DisableConventionalDiscovery()
                .IncludeType<TTriggerHandler>()
                .IncludeType<TItemCreatedHandler>()
                .IncludeType<QuestionHandler>()
                .IncludeType<ScheduledMessageHandler>();

            receiverRegistry.ListenAtPort(receiverPort).UseDurableInbox();

            configureReceiver(receiverRegistry);
        });
        
        await theReceiver.ResetResourceState();
        
        await initializeStorage(theSender, theReceiver);
    }

    public Task DisposeAsync()
    {
        theSender?.Dispose();
        theReceiver?.Dispose();

        return Task.CompletedTask;
    }

    private async Task cleanDatabase()
    {
        await initializeStorage(theSender, theReceiver);
        ScheduledMessageHandler.Reset();
    }

    protected virtual async Task initializeStorage(IHost sender, IHost receiver)
    {
        await theSender.ResetResourceState();

        await theReceiver.ResetResourceState();
    }

    protected abstract void configureReceiver(WolverineOptions receiverOptions);

    protected abstract void configureSender(WolverineOptions senderOptions);

    [Fact]
    public async Task can_send_message_end_to_end()
    {
        await cleanDatabase();

        var trigger = new TriggerMessage { Name = Guid.NewGuid().ToString() };

        await theSender
            .TrackActivity()
            .AlsoTrack(theReceiver)
            .WaitForMessageToBeReceivedAt<CascadedMessage>(theSender)
            .SendMessageAndWaitAsync(trigger);
    }

    protected abstract ItemCreated loadItem(IHost receiver, Guid id);


    protected abstract Task withContext(IHost sender, IMessageContext context,
        Func<IMessageContext, Task> action);

    private Task send(Func<IMessageContext, Task> action)
    {
        return withContext(theSender, theSender.Get<IMessageContext>(), action);
    }

    [Fact]
    public async Task can_send_items_durably_through_persisted_channels()
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

        senderCounts.Outgoing.ShouldBe(0, "There are still persisted, outgoing messages");
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

        received.Name.ShouldBe(item.Name, "The persisted item does not match");
    }

    private async Task assertIncomingEnvelopesIsZero()
    {
        var receiverCounts = await theReceiver.Get<IMessageStore>().Admin.FetchCountsAsync();
        if (receiverCounts.Incoming > 0)
        {
            await Task.Delay(500.Milliseconds());
            receiverCounts = await theReceiver.Get<IMessageStore>().Admin.FetchCountsAsync();
        }

        receiverCounts.Incoming.ShouldBe(0, "There are still persisted, incoming messages");
    }

    [Fact]
    public async Task can_schedule_job_durably()
    {
        await cleanDatabase();

        var item = new ItemCreated
        {
            Name = "Shoe",
            Id = Guid.NewGuid()
        };

        await send(async c => { await c.ScheduleAsync(item, 1.Hours()); });

        var persistor = theSender.Get<IMessageStore>();
        var counts = await persistor.Admin.FetchCountsAsync();

        counts.Scheduled.ShouldBe(1, $"counts.Scheduled = {counts.Scheduled}, should be 1");
    }

    protected abstract IReadOnlyList<Envelope> loadAllOutgoingEnvelopes(IHost sender);

    [Fact]
    public async Task send_scheduled_message()
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
    public async Task schedule_job_locally()
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

    [Fact]
    public async Task can_send_durably_with_receiver_down()
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

        await send(c => c.SendAsync(item).AsTask());

        var outgoing = loadAllOutgoingEnvelopes(theSender).SingleOrDefault();

        outgoing.ShouldNotBeNull("No outgoing envelopes are persisted");
        outgoing.MessageType.ShouldBe(typeof(ItemCreated).ToMessageTypeName(),
            $"Envelope message type expected {typeof(ItemCreated).ToMessageTypeName()}, but was {outgoing.MessageType}");
    }
}

public class TriggerMessage
{
    public string Name { get; set; }
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

public class ItemCreated
{
    public Guid Id;
    public string Name;
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