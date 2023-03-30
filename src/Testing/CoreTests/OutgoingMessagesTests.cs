using JasperFx.Core;
using TestMessages;
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

        var configured = theMessages.ShouldHaveMessageOfType<ConfiguredMessage>();
        configured.Options.ScheduleDelay.ShouldBe(delay);
        configured.Message.ShouldBe(inner);
    }
    
    [Fact]
    public void schedule_by_time()
    {
        var time = (DateTimeOffset)DateTime.Today;
        var inner = new Message1();
        
        theMessages.Schedule(inner, time);

        var configured = theMessages.ShouldHaveMessageOfType<ConfiguredMessage>();
        configured.Options.ScheduledTime.ShouldBe(time);
        configured.Message.ShouldBe(inner);
    }
    
}