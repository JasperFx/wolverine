using System.Text;
using JasperFx.Core;
using Microsoft.Extensions.Logging;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports.Sending;

namespace Wolverine.Transports.Tcp;

public static class WireProtocol
{
    // The first four values are the possible receive confirmation messages.
    // NOTE: "Recieved" is misspelled intentionally to preserve compatibility.
    // The original Rhino Queues protocol used this spelling: https://github.com/hibernating-rhinos/rhino-queues/blob/master/Rhino.Queues/Protocol/ProtocolConstants.cs
    public const string Received = "Recieved";
    public const string SerializationFailure = "FailDesr";
    public const string ProcessingFailure = "FailPrcs";
    public const string QueueDoesNotExist = "Qu-Exist";

    public const string Acknowledged = "Acknowledged";
    public const string Revert = "Revert";

    public static byte[] ReceivedBuffer = Encoding.Unicode.GetBytes(Received);
    public static byte[] SerializationFailureBuffer = Encoding.Unicode.GetBytes(SerializationFailure);
    public static byte[] ProcessingFailureBuffer = Encoding.Unicode.GetBytes(ProcessingFailure);
    public static byte[] QueueDoesNotExistBuffer = Encoding.Unicode.GetBytes(QueueDoesNotExist);

    public static byte[] AcknowledgedBuffer = Encoding.Unicode.GetBytes(Acknowledged);
    public static byte[] RevertBuffer = Encoding.Unicode.GetBytes(Revert);

    /// <summary>
    /// Default value for <see cref="MaxFrameSize"/> and
    /// <see cref="WolverineOptions.MaxIncomingTcpFrameSize"/> — 32 MiB.
    /// Sourcing both initializers from this constant prevents drift.
    /// </summary>
    public const int DefaultMaxFrameSize = 32 * 1024 * 1024;

    /// <summary>
    /// Maximum total byte size of a single inbound TCP frame. Defaults to
    /// 32 MiB. Wolverine publishes the configured
    /// <see cref="WolverineOptions.MaxIncomingTcpFrameSize"/> value into this
    /// property at host startup. Process-global; in a multi-host process the
    /// last host to start determines the active cap.
    /// </summary>
    public static int MaxFrameSize { get; set; } = DefaultMaxFrameSize;

    // Nothing but actually sending here. Worry about timeouts and retries somewhere
    // else
    public static async Task SendAsync(Stream stream, OutgoingMessageBatch batch, byte[]? messageBytes,
        ISenderCallback callback)
    {
        messageBytes ??= EnvelopeSerializer.Serialize(batch.Messages);
        var lengthBytes = BitConverter.GetBytes(messageBytes.Length);

        await stream.WriteAsync(lengthBytes, 0, lengthBytes.Length);

        await stream.WriteAsync(messageBytes, 0, messageBytes.Length);

        // All four of the possible receive confirmation messages are the same length: 8 characters long encoded in UTF-16.
        var confirmationBytes = await stream.ReadBytesAsync(ReceivedBuffer.Length).ConfigureAwait(false);
        if (confirmationBytes.SequenceEqual(ReceivedBuffer))
        {
            await callback.MarkSuccessfulAsync(batch);

            await stream.WriteAsync(AcknowledgedBuffer, 0, AcknowledgedBuffer.Length);
        }
        else if (confirmationBytes.SequenceEqual(ProcessingFailureBuffer))
        {
            await callback.MarkProcessingFailureAsync(batch);
        }
        else if (confirmationBytes.SequenceEqual(SerializationFailureBuffer))
        {
            await callback.MarkSerializationFailureAsync(batch);
        }
        else if (confirmationBytes.SequenceEqual(QueueDoesNotExistBuffer))
        {
            await callback.MarkQueueDoesNotExistAsync(batch);
        }
    }

    public static Task ReceiveAsync(IListener listener, ILogger logger, Stream stream, IReceiver callback)
    {
        return ReceiveAsync(listener, logger, stream, callback, null, CancellationToken.None);
    }

    /// <param name="inboxReceiptTimeout">
    ///     GH-5278. Set for a durable listener: answer the sender only once the durable receiver has stored every
    ///     envelope, waiting at most this long, and ask the sender to retry the batch if it could not
    /// </param>
    internal static async Task ReceiveAsync(IListener listener, ILogger logger, Stream stream, IReceiver callback,
        TimeSpan? inboxReceiptTimeout, CancellationToken cancellation)
    {
        Envelope[]? messages;

        try
        {
            var lengthBytes = await stream.ReadBytesAsync(sizeof(int));
            var length = BitConverter.ToInt32(lengthBytes, 0);
            if (length < 0 || length > MaxFrameSize)
            {
                throw new InvalidEnvelopeException(
                    $"TCP frame length {length} is outside the allowed range [0..{MaxFrameSize}].");
            }

            if (length == 0)
            {
                return;
            }

            var bytes = await stream.ReadBytesAsync(length);
            messages = EnvelopeSerializer.ReadMany(bytes);
        }
        catch (Exception e)
        {
            logger.LogError(e, "TCP receive failure");
            await stream.SendBufferAsync(SerializationFailureBuffer);
            return;
        }

        try
        {
            await receiveAsync(listener, logger, stream, callback, messages, inboxReceiptTimeout, cancellation);
        }
        catch (Exception ex)
        {
            logger.LogError(new MessageFailureException(messages, ex), "TCP receive failure");
            await stream.SendBufferAsync(ProcessingFailureBuffer);
        }
    }

    private static async Task receiveAsync(IListener listener, ILogger logger, Stream stream, IReceiver callback,
        Envelope[] messages, TimeSpan? inboxReceiptTimeout, CancellationToken cancellation)
    {
        // Just a ping
        if (messages.Length != 0 && messages.First().IsPing())
        {
            await stream.SendBufferAsync(ReceivedBuffer);

            await stream.ReadExpectedBufferAsync(AcknowledgedBuffer);

            return;
        }

        try
        {
            if (inboxReceiptTimeout is { } timeout)
            {
                // GH-5278. The durable receiver returns before it has stored the batch, and when the inbox write
                // fails it defers each envelope back to the listener -- which over TCP does nothing, after this
                // had already answered Received and the sender had deleted the envelopes from its outbox. Answer
                // only once the receiver says where every envelope ended up.
                var receipt = new InboxReceiptListener(listener, messages);
                await callback.ReceivedAsync(receipt, messages);

                if (!await receipt.AllStoredAsync(timeout, cancellation))
                {
                    logger.LogWarning(
                        "The durable inbox at {Uri} could not store a batch of {Count} envelope(s) received over TCP. Asking the sender to retry it",
                        listener.Address, messages.Length);
                    await stream.SendBufferAsync(ProcessingFailureBuffer);
                    return;
                }
            }
            else
            {
                await callback.ReceivedAsync(listener, messages);
            }

            await stream.SendBufferAsync(ReceivedBuffer);
            await stream.ReadExpectedBufferAsync(AcknowledgedBuffer);
        }
        catch (Exception e)
        {
            logger.LogError(e, "TCP receive failure");
            await stream.SendBufferAsync(ProcessingFailureBuffer);
        }
    }
}