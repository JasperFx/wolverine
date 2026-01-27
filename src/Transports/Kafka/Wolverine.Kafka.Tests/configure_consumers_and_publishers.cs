using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit.Abstractions;

namespace Wolverine.Kafka.Tests;

public class configure_consumers_and_publishers : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    public configure_consumers_and_publishers(ITestOutputHelper output)
    {
        _output = output;
    }

    private IHost _host;

    public async Task InitializeAsync()
    {
         _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092")
                    .ConfigureConsumers(consumer =>
                    {
                        // GroupId will be null
                    })
                    .ConfigureProducers(producer =>
                    {
                        producer.BatchSize = 111;
                    });

                // Just publish all messages to Kafka topics
                // based on the message type (or message attributes)
                // This will get fancier in the near future
                opts.PublishAllMessages().ToKafkaTopics();

                // Or explicitly make subscription rules
                opts.PublishMessage<ColorMessage>()
                    .ToKafkaTopic("colors")
                    .TopicCreation(async (c, t) =>
                    {
                        t.Specification.NumPartitions = 4;
                        await c.CreateTopicsAsync([t.Specification]);
                    })
                    
                    // Override the producer configuration for just this topic
                    .ConfigureProducer(config =>
                    {
                        config.BatchSize = 222;
                    }).SendInline();

                // Listen to topics
                opts.ListenToKafkaTopic("red")
                    .ProcessInline()
                    
                    // Override the consumer configuration for only this 
                    // topic
                    .ConfigureConsumer(config =>
                    {
                        // This will also set the Envelope.GroupId for any
                        // received messages at this topic
                        config.GroupId = "foo";
                        config.BootstrapServers = "localhost:9092";
                        
                        // Other configuration
                    }).Named("red");

                opts.ListenToKafkaTopic("green")
                    .BufferedInMemory();


                // This will direct Wolverine to try to ensure that all
                // referenced Kafka topics exist at application start up
                // time
                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    [Fact]
    public async Task can_receive_the_group_id_for_the_consumer_on_the_envelope()
    {
        Task Send(IMessageContext c) => c.EndpointFor("red").SendAsync(new RedMessage("one")).AsTask();
        var session = await _host.TrackActivity().IncludeExternalTransports().ExecuteAndWaitAsync(Send);
        
        session.Received.SingleEnvelope<RedMessage>()
            .GroupId.ShouldBe("foo");
    }

    [Fact]
    public void correctly_override_consumer_configuration()
    {
        foreach (var listener in _host.GetRuntime().Endpoints.ActiveListeners())
        {
            _output.WriteLine(listener.Uri.ToString());
        }
        
        var red = _host.GetRuntime().Endpoints.ActiveListeners()
            .Single(x => x.Uri == new Uri("kafka://topic/red")).ShouldBeOfType<ListeningAgent>()
            .Listener.ShouldBeOfType<KafkaListener>();
        
        red.Config.GroupId.ShouldBe("foo");
    }

    [Fact]
    public void use_default_confsumer_config_with_no_override()
    {
        var green = _host.GetRuntime().Endpoints.ActiveListeners()
            .Single(x => x.Uri.ToString().Contains("green")).ShouldBeOfType<ListeningAgent>()
            .Listener.ShouldBeOfType<KafkaListener>();
        
        green.Config.GroupId.ShouldBe("Wolverine.Kafka.Tests");
    }

    [Fact]
    public void override_producer_configuration()
    {
        var colors = _host.GetRuntime().Endpoints.GetOrBuildSendingAgent(new Uri("kafka://topic/colors"))
            .ShouldBeOfType<InlineSendingAgent>().Sender.ShouldBeOfType<InlineKafkaSender>();
        colors.Config.BatchSize.ShouldBe(222);
    }

}