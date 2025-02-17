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
        theHost.GetRuntime().Observer = observer;

        var router = theHost.GetRuntime().RoutingFor(typeof(M4));
        observer.Received().MessageRouted(typeof(M4), router);
    }
    
    [Fact]
    public void local_routes_for_handled_messages()
    {
        assertRoutesAre<M1>("local://messageroutingtests.m1");
        assertRoutesAre<M3>("local://messageroutingtests.m3");
        assertRoutesAre<M4>("local://messageroutingtests.m4");
    }
    
    [Fact]
    public void multiple_handlers_are_still_routed_to_one_place_in_default_mode()
    {
        assertRoutesAre<M2>("local://messageroutingtests.m2");
    }

    [Fact]
    public void respect_sticky_attributes_but_default_is_still_there_too()
    {
        assertRoutesAre<M5>("local://green/", "local://blue/", "local://messageroutingtests.m5");
    }

    [Fact]
    public void respect_sticky_attributes_no_default()
    {
        assertRoutesAre<OnlyStickyMessage>("local://purple", "local://red");
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