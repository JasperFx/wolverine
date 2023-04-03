using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using TestMessages;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests;

public class OutgoingMessagesTests
{
    private readonly OutgoingMessages theMessages = new OutgoingMessages();
    
    [Fact]
    public void add_a_simple_message()
    {
        var inner = new Message1();
        theMessages.RespondToSender(inner);
        theMessages.ShouldHaveMessageOfType<RespondToSender>()
            .Message.ShouldBeSameAs(inner);
    }

    [Fact]
    public void schedule_by_timespan()
    {
        var delay = 5.Minutes();
        var inner = new Message1();
        
        theMessages.Schedule(inner, delay);

        var configured = theMessages.ShouldHaveMessageOfType<DeliveryMessage<Message1>>();
        configured.Options.ScheduleDelay.ShouldBe(delay);
        configured.Message.ShouldBe(inner);
    }
    
    [Fact]
    public void schedule_by_time()
    {
        var time = (DateTimeOffset)DateTime.Today;
        var inner = new Message1();
        
        theMessages.Schedule(inner, time);

        var configured = theMessages.ShouldHaveMessageOfType<DeliveryMessage<Message1>>();
        configured.Options.ScheduledTime.ShouldBe(time);
        configured.Message.ShouldBe(inner);
    }


    [Fact]
    public async Task end_to_end()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var guid = Guid.NewGuid();
        var tracked = await host.InvokeMessageAndWaitAsync(new SpawningMessage(guid));

        tracked.Sent.SingleMessage<Message1>().Id.ShouldBe(guid);
        tracked.Sent.SingleMessage<Message2>().Id.ShouldBe(guid);
        tracked.Sent.SingleMessage<Message3>().ShouldNotBeNull();
    }
}

public record SpawningMessage(Guid Id);

public static class SpawningMessageHandler
{
    public static (Message3, OutgoingMessages) Handle(SpawningMessage message)
    {
        var messages = new OutgoingMessages();
        messages.Add(new Message1{Id = message.Id});
        messages.Add(new Message2{Id = message.Id});

        return (new Message3(), messages);
    }
}