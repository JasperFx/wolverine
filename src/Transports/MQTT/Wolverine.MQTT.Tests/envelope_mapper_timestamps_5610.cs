using System.Globalization;
using MQTTnet;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.MQTT.Internals;
using Xunit;

namespace Wolverine.MQTT.Tests;

public class envelope_mapper_timestamps_5610
{
    [Fact]
    public void timestamps_do_not_depend_on_the_culture()
    {
        var mapper = new MqttEnvelopeMapper(new MqttTopic("orders/created", new MqttTransport(),
            EndpointRole.Application));

        var deliverBy = new DateTimeOffset(2026, 11, 16, 18, 25, 40, TimeSpan.Zero);
        var outgoing = new Envelope
        {
            Data = [1],
            MessageType = "orders",
            SentAt = deliverBy.AddMinutes(-5),
            DeliverBy = deliverBy
        };

        var message = new MqttApplicationMessage();
        var incoming = new Envelope();

        var culture = CultureInfo.CurrentCulture;
        try
        {
            // The sender and the receiver run under different cultures
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            mapper.MapEnvelopeToOutgoing(outgoing, message);

            CultureInfo.CurrentCulture = new CultureInfo("en-US");
            mapper.MapIncomingToEnvelope(incoming, message);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }

        message.UserProperties.Single(x => x.Name == EnvelopeConstants.SentAtKey).Value
            .ShouldBe("2026-11-16T18:20:40.0000000+00:00");
        message.UserProperties.Single(x => x.Name == EnvelopeConstants.DeliverByKey).Value
            .ShouldBe("2026-11-16T18:25:40.0000000+00:00");

        incoming.DeliverBy.ShouldBe(deliverBy);
    }
}
