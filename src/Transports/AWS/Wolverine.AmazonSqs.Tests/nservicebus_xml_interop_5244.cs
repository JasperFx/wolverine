using System.Text;
using Amazon.SQS.Model;
using Newtonsoft.Json;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Newtonsoft;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.AmazonSqs.Tests;

/// <summary>
///     GH-5244. The SQS NServiceBus mapper wraps the body in NServiceBus' JSON transport envelope, and used to
///     serialize the body itself with a hard-coded JSON serializer and "application/json" content type. The body
///     now follows the endpoint's serializers, so NServiceBus endpoints on XmlSerializer can be reached too.
/// </summary>
public class nservicebus_xml_interop_5244
{
    private static NServiceBusEnvelopeMapper mapperFor(bool sendXml)
    {
        var queue = new AmazonSqsTransport().Queues["nsb"];
        if (sendXml)
        {
            queue.DefaultSerializer = new NServiceBusXmlSerializer();
        }

        // What UseNServiceBusInterop() does to the endpoint
        NServiceBusInterop.UseNServiceBusSerializers(queue, new NewtonsoftSerializer(new JsonSerializerSettings()));
        return new NServiceBusEnvelopeMapper("replies", queue);
    }

    private static Envelope outgoing(SqsXmlMessage message) => new()
    {
        Message = message,
        Id = Guid.NewGuid(),
        ConversationId = Guid.NewGuid(),
        CorrelationId = "correlation",
        SentAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public void writes_an_xml_body_and_says_so_in_the_nservicebus_headers()
    {
        var mapper = mapperFor(sendXml: true);

        var body = mapper.BuildMessageBody(outgoing(new SqsXmlMessage { Name = "xml" }));

        var wrapper = JsonConvert.DeserializeObject<Dictionary<string, object>>(body)!;
        var headers = JsonConvert.DeserializeObject<Dictionary<string, string>>(wrapper["Headers"].ToString()!)!;
        headers["NServiceBus.ContentType"].ShouldBe("text/xml");

        var xml = Encoding.UTF8.GetString(Convert.FromBase64String(wrapper["Body"].ToString()!));
        xml.ShouldStartWith("<?xml version=\"1.0\"?><SqsXmlMessage ");
        xml.ShouldContain("<Name>xml</Name>");
    }

    [Fact]
    public void still_writes_json_by_default()
    {
        var mapper = mapperFor(sendXml: false);

        var body = mapper.BuildMessageBody(outgoing(new SqsXmlMessage { Name = "json" }));

        body.ShouldContain("\"NServiceBus.ContentType\":\"application/json\"");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void reads_either_format_whichever_one_it_sends(bool sendXml)
    {
        var reader = mapperFor(sendXml);

        foreach (var writeXml in new[] { true, false })
        {
            var body = mapperFor(writeXml).BuildMessageBody(outgoing(new SqsXmlMessage { Name = "either" }));

            var envelope = new Envelope();
            reader.ReadEnvelopeData(envelope, body, new Dictionary<string, MessageAttributeValue>());

            envelope.ContentType.ShouldBe(writeXml ? "text/xml" : "application/json");
            envelope.Serializer!.ContentType.ShouldBe(envelope.ContentType);
            envelope.Serializer.ReadFromData(typeof(SqsXmlMessage), envelope)
                .ShouldBeOfType<SqsXmlMessage>().Name.ShouldBe("either");
        }
    }
}

public class SqsXmlMessage
{
    public string Name { get; set; } = null!;
}
