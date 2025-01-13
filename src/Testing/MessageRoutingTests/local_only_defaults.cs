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
    public void combine_handlers_for_same_message_type_by_default()
    {
        assertRoutesAre<ColorMessage>("local://messageroutingtests.colormessage", "local://red", "local://blue/");
    }
}