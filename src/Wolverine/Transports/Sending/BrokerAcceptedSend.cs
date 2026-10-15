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
        sender = brokerSenderFor(sender, envelope);

        if (sender is BatchedSender batched)
        {
            await batched.SendThroughProtocolAsync(envelope).ConfigureAwait(false);
            return;
        }

        await sender.SendAsync(envelope).ConfigureAwait(false);
    }

    /// <summary>
    ///     Send <paramref name="envelopes" />, all bound for this sender's destination, and only return once the
    ///     broker has answered for every one of them. A <see cref="BatchedSender" /> sends them through its
    ///     transport protocol in batches of the endpoint's <see cref="Configuration.Endpoint.MessageBatchSize" />,
    ///     one broker round trip per batch, and any other sender one at a time. Never throws: the result says which
    ///     envelopes the broker accepted, and the caller leaves the outbox rows of the rest in place
    /// </summary>
    public static async Task<InlineSendResult> SendToBrokerAsync(this ISender sender, IReadOnlyList<Envelope> envelopes)
    {
        var result = new InlineSendResult();

        // A tenanted sender picks a different sender per envelope, so group by the sender that really sends
        var bySender = new List<(ISender Sender, Envelope Envelope)>(envelopes.Count);
        foreach (var envelope in envelopes)
        {
            try
            {
                bySender.Add((brokerSenderFor(sender, envelope), envelope));
            }
            catch (Exception e)
            {
                // Like a tenanted sender that requires a tenant id the envelope does not have
                result.Fail(envelope, e);
            }
        }

        foreach (var group in bySender.GroupBy(x => x.Sender))
        {
            if (group.Key is BatchedSender batched)
            {
                await batched.SendThroughProtocolAsync(group.Select(x => x.Envelope).ToArray(), result)
                    .ConfigureAwait(false);
                continue;
            }

            foreach (var (groupSender, envelope) in group)
            {
                try
                {
                    await groupSender.SendAsync(envelope).ConfigureAwait(false);
                    result.Accept(envelope);
                }
                catch (Exception e)
                {
                    result.Fail(envelope, e);
                }
            }
        }

        return result;
    }

    // A TenantedSender forwards SendAsync to the tenant's own sender, which for a BatchedSender only queues the
    // envelope in memory, so the tenant's sender has to be the one that sends
    private static ISender brokerSenderFor(ISender sender, Envelope envelope)
    {
        return sender is TenantedSender tenanted ? tenanted.SenderFor(envelope) : sender;
    }
}

/// <summary>
///     Which envelopes of a grouped inline send the broker accepted, and which ones it can never take
/// </summary>
internal class InlineSendResult
{
    private readonly HashSet<Guid> _accepted = new();
    private Dictionary<Guid, Exception>? _serializationFailures;

    /// <summary>
    ///     The first failure, for the log entry of every envelope that was not accepted
    /// </summary>
    public Exception? Exception { get; private set; }

    public bool WasAccepted(Envelope envelope) => _accepted.Contains(envelope.Id);

    /// <summary>
    ///     GH-5047. The transport reported this envelope as a serialization failure, like an SQS message over the
    ///     size limit without fragmenting: sending it again can never succeed
    /// </summary>
    public bool IsSerializationFailure(Envelope envelope, out Exception exception)
    {
        if (_serializationFailures != null && _serializationFailures.TryGetValue(envelope.Id, out exception!))
        {
            return true;
        }

        exception = null!;
        return false;
    }

    internal void Accept(Envelope envelope) => _accepted.Add(envelope.Id);

    internal void Fail(Envelope envelope, Exception? exception)
    {
        _accepted.Remove(envelope.Id);
        Exception ??= exception;
    }

    internal void FailSerialization(Envelope envelope, Exception exception)
    {
        Fail(envelope, exception);
        (_serializationFailures ??= new())[envelope.Id] = exception;
    }
}

/// <summary>
///     Records what an <see cref="ISenderProtocol" /> reports for one inline batch, instead of
///     reacting to it the way a sending agent does
/// </summary>
internal class InlineSendCallback : ISenderCallback
{
    // By id: a protocol may hand back a new OutgoingMessageBatch holding just the envelopes that succeeded or
    // failed, as SQS, SNS and Azure Service Bus do when the broker accepts only part of a batch
    private readonly HashSet<Guid> _succeeded = new();
    private readonly HashSet<Guid> _failed = new();
    private HashSet<Guid>? _serializationFailed;

    public bool Succeeded { get; private set; }
    public string? Failure { get; private set; }
    public Exception? Exception { get; private set; }

    public Task MarkSuccessfulAsync(OutgoingMessageBatch outgoing)
    {
        Succeeded = true;
        foreach (var envelope in outgoing.Messages) _succeeded.Add(envelope.Id);
        return Task.CompletedTask;
    }

    public Task MarkTimedOutAsync(OutgoingMessageBatch outgoing) => fail(outgoing, "timed out");

    public Task MarkSerializationFailureAsync(OutgoingMessageBatch outgoing)
    {
        // GH-5047. Unlike every other failure, retrying cannot help, so the caller dead letters these
        _serializationFailed ??= new();
        foreach (var envelope in outgoing.Messages) _serializationFailed.Add(envelope.Id);
        return fail(outgoing, "failed to serialize");
    }

    public Task MarkQueueDoesNotExistAsync(OutgoingMessageBatch outgoing) => fail(outgoing, "the queue does not exist");

    public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing) => fail(outgoing, "failed");

    public Task MarkProcessingFailureAsync(OutgoingMessageBatch outgoing, Exception? exception)
    {
        Exception ??= exception;
        return fail(outgoing, "failed");
    }

    public Task MarkSenderIsLatchedAsync(OutgoingMessageBatch outgoing) => fail(outgoing, "the sender is latched");

    private Task fail(OutgoingMessageBatch outgoing, string reason)
    {
        Failure ??= reason;
        foreach (var envelope in outgoing.Messages) _failed.Add(envelope.Id);
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Accepted only when the protocol reported it successful and never reported it failed. An envelope the
    ///     protocol said nothing about was not accepted
    /// </summary>
    public bool WasAccepted(Envelope envelope) => _succeeded.Contains(envelope.Id) && !_failed.Contains(envelope.Id);

    /// <summary>
    ///     GH-5047. The protocol reported this envelope as one the broker can never take
    /// </summary>
    public bool IsSerializationFailure(Envelope envelope) => _serializationFailed?.Contains(envelope.Id) == true;

    public InlineSendFailedException FailureFor(Envelope envelope)
    {
        if (IsSerializationFailure(envelope))
        {
            return new InlineSendFailedException(
                $"Sending envelope {envelope} to {envelope.Destination} inline failed to serialize, so the transport can never send it",
                Exception);
        }

        var reason = Failure ?? "was never confirmed by the broker";
        return new InlineSendFailedException(
            $"Sending envelope {envelope} to {envelope.Destination} inline {reason}", Exception);
    }

    public void AssertAccepted(Envelope envelope)
    {
        if (Succeeded && Failure == null) return;

        throw FailureFor(envelope);
    }
}

public class InlineSendFailedException : Exception
{
    public InlineSendFailedException(string message, Exception? inner) : base(message, inner)
    {
    }
}
