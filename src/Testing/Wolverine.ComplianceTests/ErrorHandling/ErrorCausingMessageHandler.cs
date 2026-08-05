using Wolverine;

namespace Wolverine.ComplianceTests.ErrorHandling;

public class ErrorCausingMessageHandler
{
    public void Handle(ErrorCausingMessage message, Envelope envelope, AttemptTracker tracker)
    {
        tracker.LastAttempt = envelope.Attempts;

        if (!message.Errors.TryGetValue(envelope.Attempts, out var typeName))
        {
            message.WasProcessed = true;

            return;
        }

        throw rehydrate(typeName);
    }

    /// <summary>
    /// GH-3800. The message carries an exception TYPE NAME, not an instance, so that the error
    /// injection survives any serializer the compliance battery runs under — <c>System.Text.Json</c>
    /// (and therefore CloudEvents) cannot round-trip an Exception, and used to hand this handler a
    /// corrupted dictionary that made it throw the wrong type.
    ///
    /// <para>A failure to resolve or construct the type is thrown loudly rather than swallowed: a
    /// silently-wrong exception type is exactly the failure mode this replaced, and it presents as
    /// an error-handling rule that mysteriously does not match.</para>
    /// </summary>
    private static Exception rehydrate(string typeName)
    {
        var type = Type.GetType(typeName)
                   ?? throw new InvalidOperationException(
                       $"ErrorCausingMessage asked for exception type '{typeName}', which could not be resolved in the receiving process.");

        if (Activator.CreateInstance(type) is not Exception exception)
        {
            throw new InvalidOperationException(
                $"ErrorCausingMessage asked for exception type '{typeName}', which is not an Exception.");
        }

        return exception;
    }
}
