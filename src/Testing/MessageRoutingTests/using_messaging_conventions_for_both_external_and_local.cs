using NSubstitute;
using Wolverine;
using Wolverine.RabbitMQ;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace MessageRoutingTests;

public class using_messaging_conventions_for_both_external_and_local : MessageRoutingContext
{
    protected override void configure(WolverineOptions opts)
    {
        opts.UseRabbitMq().AutoProvision().UseConventionalRouting();
    }

    [Fact]
    public void calls_the_observer_on_new_message_routers()
    {
        var observer = Substitute.For<IWolverineObserver>();
        var runtime = theHost.GetRuntime();
        runtime.Observer = observer;

        // PrepopulateRoutingCache (#2769) primed the router cache at startup
        // with the existing observer. Clear M4 so the post-swap RoutingFor call
        // takes the cache-miss path and fires Observer.MessageRouted against
        // the substituted observer.
        runtime.ClearRoutingFor(typeof(M4));

        var router = runtime.RoutingFor(typeof(M4));
        observer.Received().MessageRouted(typeof(M4), router);
    }
    
    [Fact]
    public void local_routes_for_handled_messages()
    {
        assertRoutesAre<M1>("local://messageroutingtests.m1");
        assertRoutesAre<M3>("local://messageroutingtests.m3");
        assertRoutesAre<M4>("local://messageroutingtests.m4");
    }
    
    // GH-5136. One handler chain per handler type, and the conventional routing gives each chain that is bound
    // to a queue of its own -- sticky, or one of several handler types -- a broker queue named for its handler
    // type too: the topology MultipleHandlerBehavior.Separated built in 6.x
    [Fact]
    public void multiple_handlers_each_get_their_own_local_and_broker_queue_by_default()
    {
        assertRoutesAre<M2>("local://messageroutingtests.anotherm2handler", "local://messageroutingtests.mainm2handler",
            "local://messageroutingtests.otherm2handler", "rabbitmq://queue/MessageRoutingTests.AnotherM2Handler",
            "rabbitmq://queue/MessageRoutingTests.MainM2Handler", "rabbitmq://queue/MessageRoutingTests.OtherM2Handler");
    }

    [Fact]
    public void respect_sticky_attributes_and_the_unsticky_handler_gets_its_own_queue()
    {
        assertRoutesAre<M5>("local://green/", "local://blue/", "local://messageroutingtests.mhandler",
            "rabbitmq://queue/MessageRoutingTests.BlueM5Handler", "rabbitmq://queue/MessageRoutingTests.GreenM5Handler",
            "rabbitmq://queue/MessageRoutingTests.MHandler");
    }

    [Fact]
    public void respect_sticky_attributes_no_default()
    {
        assertRoutesAre<OnlyStickyMessage>("local://purple", "local://red",
            "rabbitmq://queue/MessageRoutingTests.PurpleOnlyStickMessageHandler",
            "rabbitmq://queue/MessageRoutingTests.RedOnlyStickMessageHandler");
    }

    [Fact]
    public void route_un_handled_messages_to_external_broker()
    {
        assertRoutesAre<NotLocallyHandled6>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled6");
        assertRoutesAre<NotLocallyHandled7>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled7");
        assertRoutesAre<NotLocallyHandled8>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled8");
        assertRoutesAre<NotLocallyHandled9>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled9");
    }
}