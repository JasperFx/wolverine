using Wolverine;
using Xunit;

namespace MessageRoutingTests;

public class publish_message_with_type_parameter : MessageRoutingContext
{
    protected override void configure(WolverineOptions opts)
    {
        opts.PublishMessage(typeof(M1)).ToLocalQueue("blue");
    }
    
    [Fact]
    public void locally_handled_messages_get_overridden_by_routing()
    {
        assertRoutesAre<M1>("local://blue");
    }
}