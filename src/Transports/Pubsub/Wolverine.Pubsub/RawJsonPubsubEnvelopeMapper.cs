// using System.Text.Json;
// using Google.Protobuf;
// using Google.Protobuf.Collections;
// using Wolverine.Transports;
// using Wolverine.Util;

// namespace Wolverine.Pubsub;

// internal class RawJsonPubsubEnvelopeMapper : IPubsubEnvelopeMapper {
//     private readonly Type _defaultMessageType;
//     private readonly JsonSerializerOptions _serializerOptions;

//     public RawJsonPubsubEnvelopeMapper(Type defaultMessageType, JsonSerializerOptions serializerOptions) {
//         _defaultMessageType = defaultMessageType;
//         _serializerOptions = serializerOptions;
//     }

//     public ByteString BuildData(Envelope envelope) => ByteString.CopyFromUtf8(JsonSerializer.Serialize(
//         envelope.Message,
//         _defaultMessageType,
//         _serializerOptions
//     ));

//     public IEnumerable<KeyValuePair<string, string>> ToAttributes(Envelope envelope) {
//         yield return new KeyValuePair<string, string>(TransportConstants.ProtocolVersion, "1.0");
//     }

//     public void ReadEnvelopeData(Envelope envelope, ByteString data, MapField<string, string> attributes) {
//         // assuming json serialized message
//         envelope.MessageType = _defaultMessageType.ToMessageTypeName();
//         envelope.Message = JsonSerializer.Deserialize(
//             data.ToStringUtf8(),
//             _defaultMessageType,
//             _serializerOptions
//         );
//     }
// }
