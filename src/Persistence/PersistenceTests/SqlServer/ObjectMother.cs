using System;
using Wolverine.Transports;

namespace Wolverine.Persistence.Testing.SqlServer;

public static class ObjectMother
{
    public static Envelope Envelope()
    {
        return new Envelope
        {
            Id = Guid.NewGuid(),
            Data = new byte[] { 1, 2, 3, 4 },
            MessageType = "Something",
            Destination = TransportConstants.ScheduledUri,
            ContentType = EnvelopeConstants.JsonContentType,
            Source = "SomeApp",
            DeliverBy = new DateTimeOffset(DateTime.Today.AddHours(4)),
            OwnerId = 567
        };
    }
}
