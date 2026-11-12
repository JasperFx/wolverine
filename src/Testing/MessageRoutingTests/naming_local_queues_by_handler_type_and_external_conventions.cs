using Wolverine;
using Wolverine.RabbitMQ;
using Xunit;

namespace MessageRoutingTests;

// GH-5518. NameQueuesByHandlerType() renames local queues only. The broker conventions name their listeners exactly
// as they do without it: a message type with one handler type keeps a listener named for the message type
public class naming_local_queues_by_handler_type_and_external_conventions : MessageRoutingContext
{
    protected override void configure(WolverineOptions opts)
    {
        opts.UseRabbitMq().AutoProvision().UseConventionalRouting();
        opts.Policies.ConfigureConventionalLocalRouting().NameQueuesByHandlerType();
    }

    [Fact]
    public void broker_listeners_are_the_same_as_without_the_option()
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

    [Fact]
    public void a_message_type_with_one_handler_type_runs_on_the_handler_types_local_queue()
    {
        assertRoutesAre<M1>("local://messageroutingtests.mhandler");
        assertRoutesAre<M3>("local://messageroutingtests.mhandler");
        assertRoutesAre<M4>("local://messageroutingtests.mhandler");
    }

    [Fact]
    public void several_handler_types_route_the_same_as_without_the_option()
    {
        assertRoutesAre<M2>("local://messageroutingtests.anotherm2handler", "local://messageroutingtests.mainm2handler",
            "local://messageroutingtests.otherm2handler", "rabbitmq://queue/MessageRoutingTests.AnotherM2Handler",
            "rabbitmq://queue/MessageRoutingTests.MainM2Handler", "rabbitmq://queue/MessageRoutingTests.OtherM2Handler");
    }

    [Fact]
    public void sticky_handlers_route_the_same_as_without_the_option()
    {
        assertRoutesAre<OnlyStickyMessage>("local://purple", "local://red",
            "rabbitmq://queue/MessageRoutingTests.PurpleOnlyStickMessageHandler",
            "rabbitmq://queue/MessageRoutingTests.RedOnlyStickMessageHandler");
    }

    [Fact]
    public void unhandled_messages_still_go_to_the_broker()
    {
        assertRoutesAre<NotLocallyHandled6>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled6");
    }
}
