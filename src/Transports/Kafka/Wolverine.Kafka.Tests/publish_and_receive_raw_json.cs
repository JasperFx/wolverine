using System.Text.Json;
using Confluent.Kafka;
using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Oakton.Resources;
using Shouldly;
using Wolverine.Kafka.Internals;
using Wolverine.Postgresql;
using Wolverine.Tracking;

namespace Wolverine.Kafka.Tests;

public class publish_and_receive_raw_json : IAsyncLifetime
{
    private IHost _sender;
    private IHost _receiver;

    public async Task InitializeAsync()
    {
        #region sample_raw_json_sending_and_receiving_with_kafka

        _receiver = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                //opts.EnableAutomaticFailureAcks = false;
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.ListenToKafkaTopic("json")
                    
                    // You do have to tell Wolverine what the message type
                    // is that you'll receive here so that it can deserialize the 
                    // incoming data
                    .ReceiveRawJson<ColorMessage>();

                opts.Services.AddResourceSetupOnStartup();
                
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "kafka");

                opts.Services.AddResourceSetupOnStartup();
                
                opts.Policies.UseDurableInboxOnAllListeners();
            }).StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseKafka("localhost:9092").AutoProvision();
                opts.Policies.DisableConventionalLocalRouting();

                opts.Services.AddResourceSetupOnStartup();

                opts.PublishAllMessages().ToKafkaTopic("json")
                    
                    // Just publish the outgoing information as pure JSON
                    // and no other Wolverine metadata
                    .PublishRawJson(new JsonSerializerOptions());
            }).StartAsync();

        #endregion
    }

    [Fact]
    public async Task can_receive_pure_json_if_the_default_messsage_type_exists()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .WaitForMessageToBeReceivedAt<ColorMessage>(_receiver)
            .PublishMessageAndWaitAsync(new ColorMessage("yellow"));

        session.Received.SingleMessage<ColorMessage>()
            .Color.ShouldBe("yellow");
    }

    [Fact]
    public async Task do_not_go_into_infinite_loop_with_garbage_data()
    {
        var transport = _sender.GetRuntime().Options.Transports.GetOrCreate<KafkaTransport>();
        var producerBuilder = new ProducerBuilder<string, string>(transport.ProducerConfig);
        using var producer = producerBuilder.Build();

        await producer.ProduceAsync("json", new Message<string, string>
        {
            Value = "{garbage}"
        });
        producer.Flush();

        await Task.Delay(2.Minutes());
    }

    public async Task DisposeAsync()
    {
        await _sender.StopAsync();
        await _receiver.StopAsync();
    }
}