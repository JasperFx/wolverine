using Microsoft.Extensions.Hosting;
using MQTTnet.Protocol;

namespace Wolverine.MQTT.Tests;

public class Samples
{
    public static async Task use_mqtt()
    {
        #region sample_using_mqtt

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // Connect to the MQTT broker
                opts.UseMqtt(builder =>
                {
                    var mqttServer = context.Configuration["mqtt_server"];

                    builder
                        .WithMaxPendingMessages(3)
                        .WithClientOptions(client =>
                        {
                            client.WithTcpServer(mqttServer);
                        });
                });


                // Listen to an MQTT topic, and this could also be a wildcard
                // pattern
                opts.ListenToMqttTopic("app/incoming")
                    
                    // The default is AtLeastOnce
                    .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);

                // Publish messages to an outbound topic
                opts.PublishAllMessages()
                    .ToMqttTopic("app/outgoing");
            })
            .StartAsync();

        #endregion
    }

    
    public static async Task publish_to_topics()
    {
        #region sample_stream_events_to_mqtt_topics

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine((context, opts) =>
            {
                // Connect to the MQTT broker
                opts.UseMqtt(builder =>
                {
                    var mqttServer = context.Configuration["mqtt_server"];

                    builder
                        .WithMaxPendingMessages(3)
                        .WithClientOptions(client =>
                        {
                            client.WithTcpServer(mqttServer);
                        });
                });

                // Publish messages to MQTT topics based on
                // the message type
                opts.PublishAllMessages()
                    .ToMqttTopics()
                    .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);
            })
            .StartAsync();

        #endregion
    }

    #region sample_broadcast_to_mqtt

    public static async Task broadcast(IMessageBus bus)
    {
        var paymentMade = new PaymentMade(200, "EUR");
        await bus.BroadcastToTopicAsync("region/europe/incoming", paymentMade);
    }

    #endregion
}

public record PaymentMade(int Amount, string Currency);