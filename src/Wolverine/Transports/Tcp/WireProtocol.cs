using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Wolverine.Transports.Util;
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


    public static async Task ReceiveAsync(IListener listener, ILogger logger, Stream stream, IReceiver callback)
    {
        Envelope[]? messages;

        try
        {
            var lengthBytes = await stream.ReadBytesAsync(sizeof(int));
            var length = BitConverter.ToInt32(lengthBytes, 0);
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
            await receiveAsync(listener, logger, stream, callback, messages);
        }
        catch (Exception ex)
        {
            logger.LogError(new MessageFailureException(messages, ex), "TCP receive failure");
            await stream.SendBufferAsync(ProcessingFailureBuffer);
        }
    }

    private static async Task receiveAsync(IListener listener, ILogger logger, Stream stream, IReceiver callback,
        Envelope[] messages)
    {
        // Just a ping
        if (messages.Any() && messages.First().IsPing())
        {
            await stream.SendBufferAsync(ReceivedBuffer);

            await stream.ReadExpectedBufferAsync(AcknowledgedBuffer);

            return;
        }

        try
        {
            await callback.ReceivedAsync(listener, messages);
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
