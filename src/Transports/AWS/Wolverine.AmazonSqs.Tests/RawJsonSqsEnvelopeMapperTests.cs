using System.Text.Json;
using Amazon.SQS.Model;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Util;

namespace Wolverine.AmazonSqs.Tests
{
    public class RawJsonSqsEnvelopeMapperTests
    {
        [Fact]
        public void deserializes_json_message_with_specified_type()
        {
            var sut = new RawJsonSqsEnvelopeMapper(typeof(TextDetected), new JsonSerializerOptions());

            string body = @"{
	""JobId"": ""fe0ffd41549670f4190cd4c07c94141aa9cb79519a23056721857e3157cd8bf1"",
	""Status"": ""SUCCEEDED"",
	""API"": ""StartDocumentTextDetection"",
	""Timestamp"": 1692958398261,
	""DocumentLocation"": {
		""S3ObjectName"": ""my.pdf"",
		""S3Bucket"": ""mybucket""
	}
}";
            Message nativeSqsMessage = new Message
            {
                Body = body,
            };

            AmazonSqsEnvelope envelope = new AmazonSqsEnvelope(nativeSqsMessage);

            sut.ReadEnvelopeData(envelope, nativeSqsMessage.Body, nativeSqsMessage.MessageAttributes);

            Assert.Equal(typeof(TextDetected).ToMessageTypeName(), envelope.MessageType);
        }

        public class TextDetected
        {
            public string JobId { get; set; } = string.Empty;

            public string JobTag { get; set; } = string.Empty;

            public string Status { get; set; } = string.Empty;

            public DocumentLocation DocumentLocation { get; set; }

            public long Timestamp { get; set; } = -1;

            public string Api { get; set; } = string.Empty;
        }

        public class DocumentLocation
        {
            public string S3ObjectName { get; set; } = string.Empty;

            public string S3Bucket { get; set; } = string.Empty;
        }
    }
}
