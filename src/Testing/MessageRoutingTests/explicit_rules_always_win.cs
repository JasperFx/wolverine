using Wolverine;
using Wolverine.RabbitMQ;
using Xunit;

namespace MessageRoutingTests;

public class explicit_rules_always_win : MessageRoutingContext
{
    protected override void configure(WolverineOptions opts)
    {
        opts.PublishMessage<M1>().ToLocalQueue("blue");
        opts.PublishMessage<M2>().ToLocalQueue("blue");
        opts.PublishMessage<M3>().ToRabbitExchange("outgoing");
        opts.PublishMessage<M3>().ToLocalQueue("blue");

        opts.PublishMessage<NotLocallyHandled6>().ToRabbitQueue("one");
        opts.PublishMessage<NotLocallyHandled6>().ToRabbitQueue("two");
        opts.PublishMessage<NotLocallyHandled7>().ToRabbitQueue("one");
        
        opts.UseRabbitMq().AutoProvision().UseConventionalRouting();
    }

    [Fact]
    public void locally_handled_messages_get_overridden_by_routing()
    {
        assertRoutesAre<M1>("local://blue");
        assertRoutesAre<M2>("local://blue");
        assertRoutesAre<M3>("local://blue", "rabbitmq://exchange/outgoing");
    }

    [Fact]
    public void explicitly_routed_messages_do_not_fall_through()
    {
        assertRoutesAre<NotLocallyHandled6>("rabbitmq://queue/one", "rabbitmq://queue/two");
        assertRoutesAre<NotLocallyHandled7>("rabbitmq://queue/one");
    }

    [Fact]
    public void unhandled_messages_with_no_explicit_rules_fall_through_to_external_broker_convention()
    {
        assertRoutesAre<NotLocallyHandled8>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled8");
        assertRoutesAre<NotLocallyHandled9>("rabbitmq://exchange/MessageRoutingTests.NotLocallyHandled9");
    }
}