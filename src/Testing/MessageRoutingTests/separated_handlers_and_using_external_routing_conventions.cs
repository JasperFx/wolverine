using Wolverine;
using Wolverine.RabbitMQ;
using Xunit;

namespace MessageRoutingTests;

public class separated_handlers_and_using_external_routing_conventions : MessageRoutingContext
{
    protected override void configure(WolverineOptions opts)
    {
        opts.UseRabbitMq().AutoProvision().UseConventionalRouting();
        opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
    }
    
        
    [Fact]
    public void should_have_a_listener_for_each_handler_if_more_than_one()
    {
        assertExternalListenersAre(@"
rabbitmq://queue/MessageRoutingTests.AnotherM2Handler
rabbitmq://queue/MessageRoutingTests.BlueColorMessageHandler
rabbitmq://queue/MessageRoutingTests.BlueM5Handler
rabbitmq://queue/MessageRoutingTests.GreenColorMessageHandler
rabbitmq://queue/MessageRoutingTests.GreenM5Handler
rabbitmq://queue/MessageRoutingTests.M1
rabbitmq://queue/MessageRoutingTests.M3
rabbitmq://queue/MessageRoutingTests.M4
rabbitmq://queue/MessageRoutingTests.MainM2Handler
rabbitmq://queue/MessageRoutingTests.MHandler
rabbitmq://queue/MessageRoutingTests.OtherM2Handler
rabbitmq://queue/MessageRoutingTests.PurpleOnlyStickMessageHandler
rabbitmq://queue/MessageRoutingTests.RedColorMessageHandler
rabbitmq://queue/MessageRoutingTests.RedOnlyStickMessageHandler
");
    }
}