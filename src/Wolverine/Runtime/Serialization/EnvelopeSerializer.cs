using System.Globalization;
using System.Xml;

namespace Wolverine.Runtime.Serialization;

public static class EnvelopeSerializer
{
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
        var numberOfMessages = br.ReadInt32();
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
        var headerCount = br.ReadInt32();

        for (var j = 0; j < headerCount; j++)
        {
            ReadDataElement(envelope, br.ReadString(), br.ReadString());
        }

        var byteCount = br.ReadInt32();
        envelope.Data = br.ReadBytes(byteCount);
    }

    private static Envelope readSingle(BinaryReader br)
    {
        var msg = new Envelope
        {
            SentAt = DateTime.FromBinary(br.ReadInt64())
        };

        var headerCount = br.ReadInt32();

        for (var j = 0; j < headerCount; j++)
        {
            ReadDataElement(msg, br.ReadString(), br.ReadString());
        }

        var byteCount = br.ReadInt32();
        msg.Data = br.ReadBytes(byteCount);

        return msg;
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

            count++;
            writer.Write(pair.Key);
            writer.Write(pair.Value);
        }

        return count;
    }
}