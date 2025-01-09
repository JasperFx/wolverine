using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.RabbitMQ;
using Xunit;

namespace MessageRoutingTests;

public class using_additive_local_routing_and_external_conventions : MessageRoutingContext
{
    public static async Task configure_additive_routing()
    {
        #region sample_additive_local_and_external_routing_conventions

        var builder = Host.CreateApplicationBuilder();
        builder.UseWolverine(opts =>
        {
            var rabbitConnectionString = builder
                .Configuration.GetConnectionString("rabbitmq");

            opts.UseRabbitMq(rabbitConnectionString)
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
    public void should_have_a_listener_for_each_message_type()
    {
        assertExternalListenersAre(@"
rabbitmq://queue/MessageRoutingTests.ColorMessage
rabbitmq://queue/MessageRoutingTests.M1
rabbitmq://queue/MessageRoutingTests.M2
rabbitmq://queue/MessageRoutingTests.M3
rabbitmq://queue/MessageRoutingTests.M4
rabbitmq://queue/MessageRoutingTests.M5
");
    }
}