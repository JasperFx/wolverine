using Wolverine;
using Wolverine.Transports;

namespace PersistenceTests.SqlServer;

public static class ObjectMother
{
    public static Envelope Envelope()
    {
        return new Envelope
        {
            Id = Guid.NewGuid(),
            Data = new byte[] { 1, 2, 3, 4 },
            MessageType = "Something",
            Destination = TransportConstants.RepliesUri,
            ContentType = EnvelopeConstants.JsonContentType,
            Source = "SomeApp",
            DeliverBy = new DateTimeOffset(DateTime.Today.AddHours(4)),
            OwnerId = 567
        };
    }
}