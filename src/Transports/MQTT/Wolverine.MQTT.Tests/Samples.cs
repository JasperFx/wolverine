using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using MQTTnet;
using MQTTnet.Protocol;
using TestMessages;
using Wolverine.Util;

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
                    // In the case of receiving JSON data, but
                    // not identifying metadata, tell Wolverine
                    // to assume the incoming message is this type
                    .DefaultIncomingMessage<Message1>()
                    
                    
                    // The default is AtLeastOnce
                    .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);

                // Publish messages to an outbound topic
                opts.PublishAllMessages()
                    .ToMqttTopic("app/outgoing");
            })
            .StartAsync();

        #endregion
    }

    public static async Task listen_for_raw_json()
    {
        #region sample_listen_for_raw_json_to_mqtt

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
                opts.ListenToMqttTopic("app/payments/made")
                    // In the case of receiving JSON data, but
                    // not identifying metadata, tell Wolverine
                    // to assume the incoming message is this type
                    .DefaultIncomingMessage<PaymentMade>();
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

    public static async Task apply_custom_envelope_mapper()
    {
        #region sample_applying_custom_mqtt_envelope_mapper

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
                    
                    // Tell Wolverine to map envelopes to MQTT messages
                    // with our custom strategy
                    .UseInterop(new MyMqttEnvelopeMapper())
                    
                    .QualityOfService(MqttQualityOfServiceLevel.AtMostOnce);
            })
            .StartAsync();

        #endregion
    }

    public static async Task publish_by_topic_rules()
    {



        #region sample_mqtt_topic_rules

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

                // Publish any message that implements ITenantMessage to 
                // MQTT with a topic derived from the message
                opts.PublishMessagesToMqttTopic<ITenantMessage>(m => $"{m.GetType().Name.ToLower()}/{m.TenantId}")
                    
                    // Specify or configure sending through Wolverine for all
                    // MQTT topic broadcasting
                    .QualityOfService(MqttQualityOfServiceLevel.ExactlyOnce)
                    .BufferedInMemory();
            })
            .StartAsync();

        #endregion
    }
}

#region sample_mqtt_itenantmessage

public interface ITenantMessage
{
    string TenantId { get; }
}

#endregion

public record PaymentMade(int Amount, string Currency);

#region sample_MyMqttEnvelopeMapper

public class MyMqttEnvelopeMapper : IMqttEnvelopeMapper
{
    public void MapEnvelopeToOutgoing(Envelope envelope, MqttApplicationMessage outgoing)
    {
        // This is the only absolutely mandatory item
        outgoing.PayloadSegment = envelope.Data;
        
        // Maybe enrich this more?
        outgoing.ContentType = envelope.ContentType;
    }

    public void MapIncomingToEnvelope(Envelope envelope, MqttApplicationMessage incoming)
    {
        // These are the absolute minimums necessary for Wolverine to function
        envelope.MessageType = typeof(PaymentMade).ToMessageTypeName();
        envelope.Data = incoming.PayloadSegment.Array;
        
        // Optional items
        envelope.DeliverWithin = 5.Seconds(); // throw away the message if it 
        // is not successfully processed
        // within 5 seconds
    }

    public IEnumerable<string> AllHeaders()
    {
        yield break;
    }
}

#endregion


