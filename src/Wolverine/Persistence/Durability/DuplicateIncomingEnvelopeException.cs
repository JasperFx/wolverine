using System;

namespace Wolverine.Persistence.Durability;

public class DuplicateIncomingEnvelopeException : Exception
{
    public DuplicateIncomingEnvelopeException(Guid messageId) : base(
        $"Duplicate incoming envelope with message id {messageId}")
    {
    }
}