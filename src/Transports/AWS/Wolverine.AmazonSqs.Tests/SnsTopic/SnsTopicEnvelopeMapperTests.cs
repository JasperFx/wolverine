using System.Text.Json;
using System.Web;
using Amazon.SQS.Model;
using Wolverine.AmazonSqs.Internal;
using Wolverine.AmazonSqs.Tests.RawJson;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Tests.SnsTopic;

public class SnsTopicEnvelopeMapperTests
{
    [Fact]
    public void deserialize_sns_topic_message_with_json_mapper()
    {
	    var innerMapper = new RawJsonSqsEnvelopeMapper(typeof(MyNativeJsonMessage), new JsonSerializerOptions());
	    var mapper = new SnsTopicEnvelopeMapper(innerMapper);
	    
	    var msg = new MyNativeJsonMessage();
	    
        var body = $$"""
                      {
                      "Type": "Notification", 
                      "MessageId": "2d931f06-0486-4505-b780-8c6920497a0b", 
                      "TopicArn": "arn:aws:sns:us-east-1:000000000000:test", 
                      "Message": "{{HttpUtility.JavaScriptStringEncode(JsonSerializer.Serialize(msg))}}",
                      "Timestamp": "2025-03-30T06:42:22.618Z", 
                      "UnsubscribeURL": "http", 
                      "MessageAttributes": 
                      {
                      	"wolverine-protocol-version": 
                      	{
                      		"Type": "String", 
                      		"Value": "1.0"
                      	}
                      }, 
                      "SignatureVersion": "1", 
                      "Signature": "xxx", 
                      "SigningCertURL": "http"
                      }
                      """;
        
        var nativeSqsMessage = new Message
        {
	        Body = body
        };
        var sqsEnvelope = new AmazonSqsEnvelope(nativeSqsMessage);
        
        mapper.ReadEnvelopeData(sqsEnvelope, nativeSqsMessage.Body, nativeSqsMessage.MessageAttributes);
        
        Assert.Equal(typeof(MyNativeJsonMessage).ToMessageTypeName(), sqsEnvelope.MessageType);
    }
}
