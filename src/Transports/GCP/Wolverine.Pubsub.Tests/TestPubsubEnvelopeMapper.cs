using System.Text;
using JasperFx.Core;
using Newtonsoft.Json;
using Wolverine.ComplianceTests.ErrorHandling;
using Wolverine.Configuration;
using Wolverine.Pubsub.Internal;
using Wolverine.Runtime.Serialization;

namespace Wolverine.Pubsub.Tests;

internal class TestPubsubEnvelopeMapper : PubsubEnvelopeMapper {
	private SystemTextJsonSerializer _serializer = new(SystemTextJsonSerializer.DefaultOptions());

	public TestPubsubEnvelopeMapper(Endpoint endpoint) : base(endpoint) {
		MapProperty(
			x => x.Message!,
			(e, m) => {
				if (e.Data is null || e.MessageType.IsEmpty()) return;

				if (e.MessageType.EndsWith(".ErrorCausingMessage")) {
					string jsonString = Encoding.UTF8.GetString(e.Data);

					e.Message = JsonConvert.DeserializeObject<ErrorCausingMessage>(jsonString);

					return;
				}

				var type = Type.GetType(e.MessageType);

				if (type is null) return;

				e.Message = _serializer.ReadFromData(type, e);
			},
			(e, m) => { }
		);
	}
}
