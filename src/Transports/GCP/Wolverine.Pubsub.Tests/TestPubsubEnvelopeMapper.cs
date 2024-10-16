using JasperFx.Core;
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

				var type = Type.GetType(e.MessageType);

				if (type is null) return;

				e.Message = _serializer.ReadFromData(type, e);
			},
			(e, m) => { }
		);
	}
}
