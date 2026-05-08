namespace Wolverine.ErrorHandling;

internal sealed class EnvelopeExpiredException : Exception
{
    public EnvelopeExpiredException(Envelope envelope)
        : base($"Envelope {envelope.Id} expired before processing.")
    {
    }
}
