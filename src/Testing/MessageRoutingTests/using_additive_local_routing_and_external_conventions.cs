using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;
using Xunit;

namespace MessageRoutingTests;

public class using_additive_local_routing_and_external_conventions : MessageRoutingContext
{
    private static async Task configure_additive_routing()
    {
        #region sample_additive_local_and_external_routing_conventions
        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var rabbitConnectionString = builder
                .Configuration.GetConnectionString("rabbitmq");

            opts.UseRabbitMq(rabbitConnectionString!)
                .AutoProvision()

                // Using the built in, default Rabbit MQ message routing conventions
                .UseConventionalRouting();
            
            // Allow Wolverine to *also* apply the Rabbit MQ conventional
            // routing to message types that this system can handle locally
            opts.Policies.ConventionalLocalRoutingIsAdditive();
        });

        #endregion
    }
    
    protected override void configure(WolverineOptions opts)
    {
        opts.PublishMessage<M1>().ToRabbitQueue("one");
        opts.UseRabbitMq().AutoProvision().UseConventionalRouting();
        opts.Policies.ConventionalLocalRoutingIsAdditive();
    }
    
    [Fact]
    public void local_routes_and_broker_conventional_routing_for_handled_messages()
    {
        assertRoutesAre<M3>("local://messageroutingtests.m3", "rabbitmq://exchange/MessageRoutingTests.M3");
        assertRoutesAre<M4>("local://messageroutingtests.m4", "rabbitmq://exchange/MessageRoutingTests.M4");
    }

    [Fact]
    public void explicit_rules_win()
    {
        assertRoutesAre<M1>("rabbitmq://queue/one");
    }

    [Fact]
    public void should_have_a_listener_for_each_message_type_or_handler_type()
    {
        // GH-5136. A message type with one handler type keeps its message-type queue; one with several gets a
        // queue per handler type, as do sticky handlers -- the same listeners MultipleHandlerBehavior.Separated
        // created in 6.x
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