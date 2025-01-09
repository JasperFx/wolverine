using Wolverine;
using Wolverine.RabbitMQ;
using Xunit;

namespace MessageRoutingTests;

public class using_local_routing_disabled_and_external_routing_conventions : MessageRoutingContext
{
    protected override void configure(WolverineOptions opts)
    {
        opts.UseRabbitMq().AutoProvision().UseConventionalRouting();
        
        opts.Policies.DisableConventionalLocalRouting();

        opts.PublishMessage<M1>().ToLocalQueue("one");
    }

    [Fact]
    public void explicit_routing_wins_no_matter_what()
    {
        assertRoutesAre<M1>("local://one");
    }
    
    [Fact]
    public void route_un_handled_messages_to_external_broker()
    {
        assertRoutesAre<NotLocallyHandled6>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled6");
        assertRoutesAre<NotLocallyHandled7>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled7");
        assertRoutesAre<NotLocallyHandled8>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled8");
        assertRoutesAre<NotLocallyHandled9>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled9");
    }

    [Fact]
    public void also_route_handled_messages_to_external_broker()
    {
        assertRoutesAre<M2>("rabbitmq://exchange/MessageRoutingTests.M2");
        assertRoutesAre<M3>("rabbitmq://exchange/MessageRoutingTests.M3");
        assertRoutesAre<M4>("rabbitmq://exchange/MessageRoutingTests.M4");
        assertRoutesAre<M5>("rabbitmq://exchange/MessageRoutingTests.M5");
    }
}