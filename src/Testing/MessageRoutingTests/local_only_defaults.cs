using Xunit;

namespace MessageRoutingTests;

public class local_only_defaults : MessageRoutingContext
{
    [Fact]
    public void local_routes_for_handled_messages()
    {
        assertRoutesAre<M1>("local://messageroutingtests.m1");
        assertRoutesAre<M3>("local://messageroutingtests.m3");
        assertRoutesAre<M4>("local://messageroutingtests.m4");
        
    }

    [Fact]
    public void multiple_handlers_each_get_their_own_local_queue_by_default()
    {
        // GH-5136. One handler chain per handler type, each on the local queue named for its handler type
        assertRoutesAre<M2>("local://messageroutingtests.anotherm2handler", "local://messageroutingtests.mainm2handler",
            "local://messageroutingtests.otherm2handler");
    }

    [Fact]
    public void respect_sticky_attributes_and_the_unsticky_handler_gets_its_own_queue()
    {
        // GH-5136. The handler that is not sticky is one of several handler types, so it is on the queue named
        // for its handler type rather than the message type's queue
        assertRoutesAre<M5>("local://green/", "local://blue/", "local://messageroutingtests.mhandler");
    }

    [Fact]
    public void respect_sticky_attributes_no_default()
    {
        assertRoutesAre<OnlyStickyMessage>("local://purple", "local://red");
    }

    [Fact]
    public void every_handler_type_gets_its_own_chain_and_queue_by_default()
    {
        // GH-5136. Nothing is combined any more: the same routes MultipleHandlerBehavior.Separated gave in 6.x
        assertRoutesAre<ColorMessage>("local://messageroutingtests.greencolormessagehandler", "local://red", "local://blue/");
    }
}