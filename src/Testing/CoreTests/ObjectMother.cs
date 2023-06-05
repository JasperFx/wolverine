using System;
using Wolverine.Transports;

namespace CoreTests.Messaging;

public static class ObjectMother
{
    public static Envelope Envelope()
    {
        return new Envelope
        {
            Id = Guid.NewGuid(),
            Data = new byte[] { 1, 2, 3, 4 },
            MessageType = "Something",
            Destination = TransportConstants.LocalUri,
            Attempts = 1
        };
    }
}