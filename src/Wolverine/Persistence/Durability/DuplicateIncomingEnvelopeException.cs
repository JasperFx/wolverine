namespace Wolverine.Persistence.Durability;

public class DuplicateIncomingEnvelopeException : Exception
{
    public DuplicateIncomingEnvelopeException(Envelope envelope) : base(
        $"Duplicate incoming envelope with message id {envelope.Id} at {envelope.Destination}")
    {
    }
}