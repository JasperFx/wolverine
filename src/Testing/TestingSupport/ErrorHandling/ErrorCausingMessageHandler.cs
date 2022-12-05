using Wolverine;

namespace TestingSupport.ErrorHandling;

public class ErrorCausingMessageHandler
{
    public void Handle(ErrorCausingMessage message, Envelope envelope, AttemptTracker tracker)
    {
        tracker.LastAttempt = envelope.Attempts;

        if (!message.Errors.ContainsKey(envelope.Attempts))
        {
            message.WasProcessed = true;

            return;
        }

        if (message.Errors.TryGetValue(envelope.Attempts, out var ex))
        {
            throw ex;
        }
    }
}