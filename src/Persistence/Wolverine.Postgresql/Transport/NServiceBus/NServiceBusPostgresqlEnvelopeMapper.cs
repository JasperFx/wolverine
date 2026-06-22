using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Util;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.Postgresql.Transport.NServiceBus;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class NServiceBusPostgresqlJsonContext : JsonSerializerContext;

/// <summary>
/// Translates between Wolverine <see cref="Envelope"/>s and the NServiceBus SQL transport
/// wire shape: a queue table row carrying a JSON <c>Headers</c> dictionary and a raw
/// <c>Body</c> payload. This is the documented, Particular-sanctioned "native integration"
/// contract for the PostgreSQL transport.
/// </summary>
/// <remarks>
/// The mapping deliberately mirrors the existing broker interop mappers
/// (<c>NServiceBusEnvelopeMapper</c> in Wolverine.AmazonSns / the RabbitMQ NServiceBus
/// interop) so that the same header conventions hold across every NServiceBus interop
/// transport. The body is left as raw bytes and run through Wolverine's normal serializer
/// negotiation so the receiving handler deserializes it like any other message.
/// </remarks>
internal class NServiceBusPostgresqlEnvelopeMapper
{
    // The minimal NSB header set documented for native integration.
    public const string MessageIdHeader = "NServiceBus.MessageId";
    public const string ConversationIdHeader = "NServiceBus.ConversationId";
    public const string CorrelationIdHeader = "NServiceBus.CorrelationId";
    public const string ReplyToAddressHeader = "NServiceBus.ReplyToAddress";
    public const string MessageIntentHeader = "NServiceBus.MessageIntent";
    public const string ContentTypeHeader = "NServiceBus.ContentType";
    public const string TimeSentHeader = "NServiceBus.TimeSent";
    public const string EnclosedMessageTypesHeader = "NServiceBus.EnclosedMessageTypes";

    // NServiceBus formats NServiceBus.TimeSent with this exact pattern.
    private const string TimeSentFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";

    private readonly Endpoint _endpoint;
    private readonly Func<string?> _replyAddress;

    public NServiceBusPostgresqlEnvelopeMapper(Endpoint endpoint, Func<string?> replyAddress)
    {
        _endpoint = endpoint;
        _replyAddress = replyAddress;
    }

    /// <summary>
    /// Build the row values written to the NServiceBus queue table for an outgoing envelope.
    /// </summary>
    public OutgoingRow MapOutgoing(Envelope envelope)
    {
        var serializer = envelope.Serializer ?? _endpoint.DefaultSerializer
            ?? throw new InvalidOperationException("No serializer is configured for this endpoint");

        var body = envelope.Data ?? serializer.WriteMessage(envelope.Message!);
        var contentType = envelope.ContentType ?? serializer.ContentType;

        // Pass through any "extra" Wolverine headers so they survive a round trip.
        var headers = new Dictionary<string, string>();
        foreach (var pair in envelope.Headers)
        {
            if (pair.Value != null)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        headers[MessageIdHeader] = envelope.Id.ToString();
        headers[ConversationIdHeader] = (envelope.ConversationId == Guid.Empty ? envelope.Id : envelope.ConversationId).ToString();
        headers[MessageIntentHeader] = "Send";
        headers[ContentTypeHeader] = contentType;
        headers[TimeSentHeader] = envelope.SentAt.ToUniversalTime().ToString(TimeSentFormat, CultureInfo.InvariantCulture);

        if (envelope.CorrelationId.IsNotEmpty())
        {
            headers[CorrelationIdHeader] = envelope.CorrelationId!;
        }

        var replyAddress = _replyAddress();
        if (replyAddress.IsNotEmpty())
        {
            headers[ReplyToAddressHeader] = replyAddress!;
        }

        if (envelope.Message != null)
        {
            headers[EnclosedMessageTypesHeader] = toEnclosedMessageType(envelope.Message.GetType());
        }
        else if (envelope.MessageType.IsNotEmpty())
        {
            headers[EnclosedMessageTypesHeader] = envelope.MessageType!;
        }

        var json = JsonSerializer.Serialize(headers, NServiceBusPostgresqlJsonContext.Default.DictionaryStringString);

        return new OutgoingRow(envelope.Id, json, body, envelope.DeliverBy);
    }

    /// <summary>
    /// Hydrate an <see cref="Envelope"/> from a row read off the NServiceBus queue table.
    /// </summary>
    public Envelope MapIncoming(Guid id, string? headersJson, byte[] body)
    {
        // NServiceBus' JSON serializer prefixes the body with a UTF-8 BOM, which
        // System.Text.Json rejects ('0xEF' is an invalid start of a value). Strip it so
        // the configured serializer can read the payload.
        var envelope = new Envelope { Data = stripUtf8Bom(body) };

        var headers = headersJson.IsNotEmpty()
            ? JsonSerializer.Deserialize(headersJson!, NServiceBusPostgresqlJsonContext.Default.DictionaryStringString) ?? new()
            : new Dictionary<string, string>();

        envelope.Id = readGuid(headers, MessageIdHeader) ?? id;
        if (envelope.Id == Guid.Empty) envelope.Id = id;

        var conversationId = readGuid(headers, ConversationIdHeader);
        if (conversationId.HasValue) envelope.ConversationId = conversationId.Value;

        if (headers.TryGetValue(CorrelationIdHeader, out var correlationId))
        {
            envelope.CorrelationId = correlationId;
        }

        if (headers.TryGetValue(ReplyToAddressHeader, out var replyTo) && replyTo.IsNotEmpty())
        {
            envelope.ReplyUri = NServiceBusPostgresqlQueue.ToUri(normalizeAddress(replyTo));
        }

        if (headers.TryGetValue(ContentTypeHeader, out var contentType) && contentType.IsNotEmpty())
        {
            envelope.ContentType = contentType;
        }

        if (headers.TryGetValue(TimeSentHeader, out var timeSent)
            && DateTimeOffset.TryParse(timeSent, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var sentAt))
        {
            envelope.SentAt = sentAt;
        }

        if (headers.TryGetValue(EnclosedMessageTypesHeader, out var enclosed) && enclosed.IsNotEmpty())
        {
            envelope.MessageType = resolveMessageType(enclosed);
        }

        envelope.Serializer = _endpoint.TryFindSerializer(envelope.ContentType) ?? _endpoint.DefaultSerializer;

        return envelope;
    }

    // The NServiceBus PostgreSQL transport addresses queues as the quoted, schema-qualified
    // "schema"."table" (e.g. "public"."nsb"); the SQL Server transport uses table@schema@catalog.
    // Either way we only want the bare queue/table name so it maps back to a Wolverine endpoint.
    private static string normalizeAddress(string address)
    {
        // SQL Server form: take the part before the first '@'.
        var at = address.IndexOf('@');
        if (at >= 0)
        {
            address = address[..at];
        }

        // PostgreSQL form: drop quoting and take the last dotted segment ("public"."nsb" -> nsb).
        address = address.Replace("\"", string.Empty);
        var dot = address.LastIndexOf('.');
        if (dot >= 0)
        {
            address = address[(dot + 1)..];
        }

        return address;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification =
            "Reflecting over the interfaces of a live message instance's runtime type for NServiceBus interop; the type and its interfaces are present at runtime.")]
    private static string toEnclosedMessageType(Type type)
    {
        // NServiceBus keys handler dispatch off NServiceBus.EnclosedMessageTypes, a
        // ';'-separated, most-derived-first list of the message's type hierarchy. Emit the
        // concrete type plus its implemented interfaces so that a Wolverine message defined
        // in one assembly can still bind to an NServiceBus handler registered against a
        // shared interface (e.g. IHandleMessages<ISomeInterface>) living in another.
        var names = new List<string> { format(type) };
        names.AddRange(type.GetInterfaces().Select(format));
        return string.Join(";", names);

        static string format(Type t) => $"{t.FullName}, {t.Assembly.GetName().Name}";
    }

    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification =
            "The enclosed message type name comes from a foreign NServiceBus endpoint at runtime; a failed resolution falls back to the bare type name which Wolverine binds via RegisterInteropMessageAssembly.")]
    private static string resolveMessageType(string enclosed)
    {
        // NServiceBus lists several types separated by ';' (concrete + interfaces),
        // most-derived first. Use the first entry that resolves to a type loadable in this
        // process; that is the one Wolverine can map to a handler (directly or via the
        // RegisterInteropMessageAssembly interface->concrete binding).
        var entries = enclosed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var entry in entries)
        {
            var type = Type.GetType(entry);
            if (type != null)
            {
                return type.ToMessageTypeName();
            }
        }

        // Fall back to the bare type name; Wolverine's interop assembly registration
        // can still bind it to a concrete handler.
        return entries.First().Split(',', StringSplitOptions.TrimEntries).First();
    }

    private static byte[] stripUtf8Bom(byte[] body)
    {
        return body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF
            ? body[3..]
            : body;
    }

    private static Guid? readGuid(IReadOnlyDictionary<string, string> headers, string key)
    {
        return headers.TryGetValue(key, out var raw) && Guid.TryParse(raw, out var value) ? value : null;
    }

    internal readonly record struct OutgoingRow(Guid Id, string Headers, byte[] Body, DateTimeOffset? Expires);
}
