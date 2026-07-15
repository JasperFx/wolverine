using System.Collections.Frozen;
using System.Globalization;
using System.Xml;

namespace Wolverine.Runtime.Serialization;

public static class EnvelopeSerializer
{
    /// <summary>
    ///     The header keys that <see cref="ReadDataElement" /> promotes straight back into a typed
    ///     <see cref="Envelope" /> property instead of leaving in <see cref="Envelope.Headers" />.
    ///     These are skipped when the loose <see cref="Envelope.Headers" /> are written to the wire
    ///     format so that a reserved key sitting in <c>Headers</c> — put there by a custom
    ///     <c>IEnvelopeMapper</c> copying raw broker headers, or by user code — can never overwrite
    ///     the authoritative typed property on the next read. See GH-3408.
    ///     Note that this deliberately does NOT include every constant on <see cref="EnvelopeConstants" />:
    ///     <c>causation-id</c> is intentionally carried in <c>Headers</c> (see <c>DeliveryOptions</c>) and
    ///     is never promoted by the reader, so it must keep round-tripping as an ordinary header.
    /// </summary>
    internal static readonly FrozenSet<string> ReservedHeaderKeys = new[]
    {
        EnvelopeConstants.SourceKey,
        EnvelopeConstants.MessageTypeKey,
        EnvelopeConstants.ReplyUriKey,
        EnvelopeConstants.ContentTypeKey,
        EnvelopeConstants.CorrelationIdKey,
        EnvelopeConstants.SagaIdKey,
        EnvelopeConstants.ConversationIdKey,
        EnvelopeConstants.DestinationKey,
        EnvelopeConstants.AcceptedContentTypesKey,
        EnvelopeConstants.IdKey,
        EnvelopeConstants.ParentIdKey,
        EnvelopeConstants.GroupIdKey,
        EnvelopeConstants.ReplyRequestedKey,
        EnvelopeConstants.AckRequestedKey,
        EnvelopeConstants.IsResponseKey,
        EnvelopeConstants.ExecutionTimeKey,
        EnvelopeConstants.KeepUntilKey,
        EnvelopeConstants.AttemptsKey,
        EnvelopeConstants.DeliverByKey,
        EnvelopeConstants.TenantIdKey,
        EnvelopeConstants.TopicNameKey,
        EnvelopeConstants.UserNameKey,
        EnvelopeConstants.PartitionKey
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Caps applied to inbound envelopes during deserialization. Defaults to
    /// <see cref="EnvelopeReaderLimits.Default"/>. Wolverine publishes the
    /// configured <see cref="WolverineOptions"/> values into this property
    /// at host startup. The slot is process-global; when several Wolverine
    /// hosts run in the same process, the last one to start determines the
    /// active limits.
    /// </summary>
    public static EnvelopeReaderLimits Limits { get; set; } = EnvelopeReaderLimits.Default;

    public static void ReadDataElement(Envelope env, string key, string value)
    {
        try
        {
            switch (key)
            {
                case EnvelopeConstants.SourceKey:
                    env.Source = value;
                    break;

                case EnvelopeConstants.MessageTypeKey:
                    env.MessageType = value;
                    break;

                case EnvelopeConstants.ReplyUriKey:
                    env.ReplyUri = new Uri(value);
                    break;

                case EnvelopeConstants.ContentTypeKey:
                    env.ContentType = value;
                    break;

                case EnvelopeConstants.CorrelationIdKey:
                    env.CorrelationId = value;
                    break;

                case EnvelopeConstants.SagaIdKey:
                    env.SagaId = value;
                    break;

                case EnvelopeConstants.ConversationIdKey:
                    if (Guid.TryParse(value, out var cid))
                    {
                        env.ConversationId = cid;
                    }

                    break;

                case EnvelopeConstants.DestinationKey:
                    env.Destination = new Uri(value);
                    break;

                case EnvelopeConstants.AcceptedContentTypesKey:
                    env.AcceptedContentTypes = value.Split(',');
                    break;

                case EnvelopeConstants.IdKey:
                    if (Guid.TryParse(value, out var id))
                    {
                        env.Id = id;
                    }

                    break;

                case EnvelopeConstants.ParentIdKey:
                    env.ParentId = value;
                    break;

                case EnvelopeConstants.GroupIdKey:
                    env.GroupId = value;
                    break;

                case EnvelopeConstants.ReplyRequestedKey:
                    env.ReplyRequested = value;
                    break;

                case EnvelopeConstants.AckRequestedKey:
                    env.AckRequested = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;

                case EnvelopeConstants.IsResponseKey:
                    env.IsResponse = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;

                case EnvelopeConstants.ExecutionTimeKey:
                    // Don't read it twice
                    if (env.ScheduledTime.HasValue) return;

                    try
                    {
                        env.ScheduledTime = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.Utc);
                    }
                    catch (Exception )
                    {
                        if (DateTimeOffset.TryParse(value, out var dt))
                        {
                            env.ScheduledTime = dt;
                        }
                    }
                    break;
                
                case EnvelopeConstants.KeepUntilKey:
                    // Don't read it twice
                    if (env.KeepUntil.HasValue) return;

                    try
                    {
                        env.KeepUntil = XmlConvert.ToDateTime(value, XmlDateTimeSerializationMode.Utc);
                    }
                    catch (Exception )
                    {
                        if (DateTimeOffset.TryParse(value, out var dt))
                        {
                            env.KeepUntil = dt;
                        }
                    }
                    break;

                case EnvelopeConstants.AttemptsKey:
                    env.Attempts = int.Parse(value);
                    break;

                case EnvelopeConstants.DeliverByKey:
                    env.DeliverBy = DateTime.Parse(value);
                    break;

                case EnvelopeConstants.TenantIdKey:
                    env.TenantId = value;
                    break;

                case EnvelopeConstants.TopicNameKey:
                    env.TopicName = value;
                    break;

                case EnvelopeConstants.UserNameKey:
                    env.UserName = value;
                    break;

                case EnvelopeConstants.PartitionKey:
                    env.PartitionKey = value;
                    break;

                default:
                    env.Headers.Add(key, value);
                    break;
            }
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"Error trying to read data for {key} = '{value}'", e);
        }
    }

    public static Envelope[] ReadMany(byte[] buffer)
    {
        using var ms = new MemoryStream(buffer);
        using var br = new BinaryReader(ms);
        var limits = Limits;
        var numberOfMessages = br.ReadInt32();
        if (numberOfMessages < 0 || numberOfMessages > limits.MaxBatchSize)
        {
            throw new InvalidEnvelopeException(
                $"Envelope batch size {numberOfMessages} is outside the allowed range [0..{limits.MaxBatchSize}].");
        }

        // Each envelope is at least ~16 bytes on the wire (SentAt int64 +
        // headerCount int32 + byteCount int32). Reject claims that can't fit
        // in the buffer we actually received.
        const int MinBytesPerEnvelope = 16;
        if ((long)numberOfMessages * MinBytesPerEnvelope > buffer.Length)
        {
            throw new InvalidEnvelopeException(
                $"Envelope batch size {numberOfMessages} is impossible for a {buffer.Length}-byte buffer.");
        }

        var messages = new Envelope[numberOfMessages];
        for (var i = 0; i < numberOfMessages; i++)
        {
            messages[i] = readSingle(br);
        }

        return messages;
    }

    public static Envelope Deserialize(byte[] buffer)
    {
        using var ms = new MemoryStream(buffer);
        using var br = new BinaryReader(ms);
        return readSingle(br);
    }

    public static void ReadEnvelopeData(Envelope envelope, byte[] buffer)
    {
        using var ms = new MemoryStream(buffer);
        using var br = new BinaryReader(ms);
        envelope.SentAt = DateTime.FromBinary(br.ReadInt64());
        readEnvelopeBody(br, envelope, Limits);
    }

    private static Envelope readSingle(BinaryReader br)
    {
        var msg = new Envelope
        {
            SentAt = DateTime.FromBinary(br.ReadInt64())
        };
        readEnvelopeBody(br, msg, Limits);
        return msg;
    }

    private static void readEnvelopeBody(BinaryReader br, Envelope envelope, EnvelopeReaderLimits limits)
    {
        var headerCount = br.ReadInt32();
        if (headerCount < 0 || headerCount > limits.MaxHeaderCount)
        {
            throw new InvalidEnvelopeException(
                $"Envelope header count {headerCount} is outside the allowed range [0..{limits.MaxHeaderCount}].");
        }

        for (var j = 0; j < headerCount; j++)
        {
            ReadDataElement(envelope, br.ReadString(), br.ReadString());
        }

        var byteCount = br.ReadInt32();
        if (byteCount < 0 || byteCount > limits.MaxDataSize)
        {
            throw new InvalidEnvelopeException(
                $"Envelope data size {byteCount} bytes is outside the allowed range [0..{limits.MaxDataSize}].");
        }

        var remaining = br.BaseStream.Length - br.BaseStream.Position;
        if (byteCount > remaining)
        {
            throw new InvalidEnvelopeException(
                $"Envelope claims {byteCount} bytes of data but only {remaining} remain in the buffer.");
        }

        envelope.Data = br.ReadBytes(byteCount);
    }

    public static byte[] Serialize(IList<Envelope> messages)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(messages.Count);
        foreach (var message in messages) writeSingle(writer, message);
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] Serialize(Envelope env)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writeSingle(writer, env);
        writer.Flush();
        return stream.ToArray();
    }

    private static void writeSingle(BinaryWriter writer, Envelope env)
    {
        writer.Write(env.SentAt.UtcDateTime.ToBinary());

        // Write a placeholder for the header count, then seek back to fill it in
        // after writing headers. This avoids allocating a second MemoryStream + BinaryWriter.
        var countPosition = writer.BaseStream.Position;
        writer.Write(0);

        var count = writeHeaders(writer, env);

        var endPosition = writer.BaseStream.Position;
        writer.BaseStream.Position = countPosition;
        writer.Write(count);
        writer.BaseStream.Position = endPosition;

        writer.Write(env.Data!.Length);
        writer.Write(env.Data);
    }

    private static int writeHeaders(BinaryWriter writer, Envelope env)
    {
        var count = 0;

        writer.WriteProp(ref count, EnvelopeConstants.SourceKey, env.Source);
        writer.WriteProp(ref count, EnvelopeConstants.MessageTypeKey, env.MessageType);
        writer.WriteProp(ref count, EnvelopeConstants.ReplyUriKey, env.ReplyUri);
        writer.WriteProp(ref count, EnvelopeConstants.ContentTypeKey, env.ContentType);
        writer.WriteProp(ref count, EnvelopeConstants.CorrelationIdKey, env.CorrelationId);
        writer.WriteProp(ref count, EnvelopeConstants.ConversationIdKey, env.ConversationId);
        writer.WriteProp(ref count, EnvelopeConstants.DestinationKey, env.Destination);
        writer.WriteProp(ref count, EnvelopeConstants.SagaIdKey, env.SagaId);
        writer.WriteProp(ref count, EnvelopeConstants.ParentIdKey, env.ParentId);
        writer.WriteProp(ref count, EnvelopeConstants.TenantIdKey, env.TenantId);
        writer.WriteProp(ref count, EnvelopeConstants.TopicNameKey, env.TopicName);
        writer.WriteProp(ref count, EnvelopeConstants.UserNameKey, env.UserName);

        if (env.AcceptedContentTypes.Length != 0)
        {
            writer.WriteProp(ref count, EnvelopeConstants.AcceptedContentTypesKey,
                string.Join(",", env.AcceptedContentTypes));
        }

        writer.WriteProp(ref count, EnvelopeConstants.IdKey, env.Id);
        writer.WriteProp(ref count, EnvelopeConstants.ReplyRequestedKey, env.ReplyRequested);
        writer.WriteProp(ref count, EnvelopeConstants.AckRequestedKey, env.AckRequested);
        writer.WriteProp(ref count, EnvelopeConstants.IsResponseKey, env.IsResponse);
        writer.WriteProp(ref count, EnvelopeConstants.GroupIdKey, env.GroupId);
        writer.WriteProp(ref count, EnvelopeConstants.PartitionKey, env.PartitionKey);

        if (env.ScheduledTime.HasValue)
        {
            var dateString = env.ScheduledTime.Value.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            count++;
            writer.Write(EnvelopeConstants.ExecutionTimeKey);
            writer.Write(dateString);
        }

        if (env.KeepUntil.HasValue)
        {
            var dateString = env.KeepUntil.Value.ToUniversalTime()
                .ToString("yyyy-MM-ddTHH:mm:ss.fffffff", CultureInfo.InvariantCulture);
            count++;
            writer.Write(EnvelopeConstants.KeepUntilKey);
            writer.Write(dateString);
        }

        writer.WriteProp(ref count, EnvelopeConstants.AttemptsKey, env.Attempts);
        writer.WriteProp(ref count, EnvelopeConstants.DeliverByKey, env.DeliverBy);

        foreach (var pair in env.Headers)
        {
            if (pair.Value is null)
            {
                continue;
            }

            // A reserved key sitting in the loose Headers would be written *after* the typed
            // properties above, and the reader promotes reserved keys straight back into those
            // typed properties -- so writing it would let the header silently overwrite the real
            // TenantId/SagaId/Id/etc. Skip it instead. See GH-3408.
            if (ReservedHeaderKeys.Contains(pair.Key))
            {
                continue;
            }

            count++;
            writer.Write(pair.Key);
            writer.Write(pair.Value);
        }

        return count;
    }
}