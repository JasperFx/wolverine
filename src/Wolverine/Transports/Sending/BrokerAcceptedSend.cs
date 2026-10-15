namespace Wolverine.Transports.Sending;

/// <summary>
///     GH-5034. Sends one envelope so that the returned task only completes once the broker has accepted
///     it. A <see cref="BatchedSender" /> only queues an envelope in memory and sends it later in the
///     background, so its own <see cref="ISender.SendAsync" /> proves nothing. This path goes through its
///     <see cref="ISenderProtocol" /> directly instead
/// </summary>
internal static class BrokerAcceptedSend
{
    /// <summary>
    ///     Send <paramref name="envelope" /> and only return once the broker has it. Throws if the send failed
    ///     for any reason, so the caller can leave the envelope's outbox row in place
    /// </summary>
    public static async Task SendToBrokerAsync(this ISender sender, Envelope envelope)
    {
        if (sender is BatchedSender batched)
        {
            await batched.SendThroughProtocolAsync(envelope).ConfigureAwait(false);
            return;
        }

        await sender.SendAsync(envelope).ConfigureAwait(false);
    }
}

/// <summary>
///     Records what an <see cref="ISenderProtocol" /> reports for a single inline batch, instead of
///     reacting to it the way a sending agent does
/// </summary>
internal class InlineSendCallback : ISenderCallback
{
    public bool Succeeded { get; private set; }
    public string? Failure { get; private set; }
    public Exception? Exception { get; private set; }

    public Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing)
    {
        Succeeded = true;
        return Task.CompletedTask;
    }

    public Task MarkTimedOutAsync(OutgoingMessageBatch outgoing) => fail("timed out");

    public Task MarkSerializationFailureAsync(OutgoingMessageBatch outgoing) => fail("failed to serialize");

    public Task MarkQueueDoesNotExistAsync(OutgoingMessageBatch outgoing) => fail("the queue does not exist");

    public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing) => fail("failed");

    public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing, Exception? exception)
    {
        Exception ??= exception;
        return fail("failed");
    }

    public Task MarkSenderIsLatchedAsync(OutgoingMessageBatch outgoing) => fail("the sender is latched");

    private Task fail(string reason)
    {
        Failure ??= reason;
        return Task.CompletedTask;
    }

    public void AssertAccepted(Envelope envelope)
    {
        if (Succeeded && Failure == null) return;

        var reason = Failure ?? "was never confirmed by the broker";
        throw new InlineSendFailedException(
            $"Sending envelope {envelope} to {envelope.Destination} inline {reason}", Exception);
    }
}

public class InlineSendFailedException : Exception
{
    public InlineSendFailedException(string message, Exception? inner) : base(message, inner)
    {
    }
}
