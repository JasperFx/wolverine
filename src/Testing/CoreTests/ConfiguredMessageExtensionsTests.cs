using JasperFx.Core;
using TestMessages;
using Xunit;

namespace CoreTests;

public class ConfiguredMessageExtensionsTests
{
    [Fact]
    public void delayed_for()
    {
        var delay = 5.Minutes();
        var inner = new Message1();
        var configured = inner.DelayedFor(delay);
        
        configured.Options.ScheduleDelay.ShouldBe(delay);
        configured.Message.ShouldBe(inner);
    }

    [Fact]
    public void scheduled_at()
    {
        var time = (DateTimeOffset)DateTime.Today;
        var inner = new Message1();

        var configured = inner.ScheduledAt(time);

        configured.Options.ScheduledTime.ShouldBe(time);
        configured.Message.ShouldBe(inner);
    }

    [Fact]
    public void to_endpoint()
    {
        var inner = new Message1();
        var message = inner.ToEndpoint("foo", new DeliveryOptions{DeliverWithin = 5.Seconds()});
        
        message.Message.ShouldBe(inner);
        message.EndpointName.ShouldBe("foo");
        message.DeliveryOptions.DeliverBy.HasValue.ShouldBeTrue();
    }

    [Fact]
    public void to_uri()
    {
        var inner = new Message1();
        var destination = new Uri("rabbitmq://queue/foo");
        var message = inner.ToDestination(destination, new DeliveryOptions{DeliverWithin = 5.Seconds()});
        
        message.Message.ShouldBe(inner);
        message.Destination.ShouldBe(destination);
        message.DeliveryOptions.DeliverBy.HasValue.ShouldBeTrue();
    }
    
    
}