namespace Wolverine.ErrorHandling;

internal sealed class EnvelopeDiscardedException : Exception
{
    public EnvelopeDiscardedException(Envelope envelope)
        : base($"Envelope {envelope.Id} was discarded by error policy.")
    {
    }
}
