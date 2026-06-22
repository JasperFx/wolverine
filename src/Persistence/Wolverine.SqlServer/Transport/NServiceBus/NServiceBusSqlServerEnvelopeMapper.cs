using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Util;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.SqlServer.Transport.NServiceBus;

[JsonSerializable(typeof(Dictionary<string, string>))]
internal partial class NServiceBusSqlJsonContext : JsonSerializerContext;

/// <summary>
/// Translates between Wolverine <see cref="Envelope"/>s and the NServiceBus SQL transport
/// wire shape: a queue table row carrying a JSON <c>Headers</c> dictionary and a raw
/// <c>Body</c> payload. This is the documented, Particular-sanctioned "native integration"
/// contract for the SQL Server transport.
/// </summary>
/// <remarks>
/// The mapping deliberately mirrors the existing broker interop mappers
/// (<c>NServiceBusEnvelopeMapper</c> in Wolverine.AmazonSns / the RabbitMQ NServiceBus
/// interop) so that the same header conventions hold across every NServiceBus interop
/// transport. The body is left as raw bytes and run through Wolverine's normal serializer
/// negotiation so the receiving handler deserializes it like any other message.
/// </remarks>
internal class NServiceBusSqlServerEnvelopeMapper
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

    public NServiceBusSqlServerEnvelopeMapper(Endpoint endpoint, Func<string?> replyAddress)
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
            headers[EnclosedMessageTypesHeader] = ToEnclosedMessageType(envelope.Message.GetType());
        }
        else if (envelope.MessageType.IsNotEmpty())
        {
            headers[EnclosedMessageTypesHeader] = envelope.MessageType!;
        }

        var json = JsonSerializer.Serialize(headers, NServiceBusSqlJsonContext.Default.DictionaryStringString);

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
        var envelope = new Envelope { Data = StripUtf8Bom(body) };

        var headers = headersJson.IsNotEmpty()
            ? JsonSerializer.Deserialize(headersJson!, NServiceBusSqlJsonContext.Default.DictionaryStringString) ?? new()
            : new Dictionary<string, string>();

        envelope.Id = ReadGuid(headers, MessageIdHeader) ?? id;
        if (envelope.Id == Guid.Empty) envelope.Id = id;

        var conversationId = ReadGuid(headers, ConversationIdHeader);
        if (conversationId.HasValue) envelope.ConversationId = conversationId.Value;

        if (headers.TryGetValue(CorrelationIdHeader, out var correlationId))
        {
            envelope.CorrelationId = correlationId;
        }

        if (headers.TryGetValue(ReplyToAddressHeader, out var replyTo) && replyTo.IsNotEmpty())
        {
            envelope.ReplyUri = NServiceBusSqlServerQueue.ToUri(NormalizeAddress(replyTo));
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
            envelope.MessageType = ResolveMessageType(enclosed);
        }

        envelope.Serializer = _endpoint.TryFindSerializer(envelope.ContentType) ?? _endpoint.DefaultSerializer;

        return envelope;
    }

    // NServiceBus addresses take the form "table@schema@catalog"; we only care about the table/queue name.
    private static string NormalizeAddress(string address)
    {
        var at = address.IndexOf('@');
        return at >= 0 ? address[..at] : address;
    }

    private static string ToEnclosedMessageType(Type type)
    {
        // NServiceBus resolves enclosed message types via Type.GetType; emit a
        // "FullName, AssemblyName" form so the foreign endpoint can bind the handler.
        return $"{type.FullName}, {type.Assembly.GetName().Name}";
    }

    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification =
            "The enclosed message type name comes from a foreign NServiceBus endpoint at runtime; a failed resolution falls back to the bare type name which Wolverine binds via RegisterInteropMessageAssembly.")]
    private static string ResolveMessageType(string enclosed)
    {
        // NServiceBus may list several types separated by ';' (concrete + interfaces).
        // The first entry is the most-derived concrete type.
        var first = enclosed.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).First();

        var type = Type.GetType(first);
        if (type != null)
        {
            return type.ToMessageTypeName();
        }

        // Fall back to the bare type name; Wolverine's interop assembly registration
        // (RegisterInteropMessageAssembly) can still bind it to a concrete handler.
        return first.Split(',', StringSplitOptions.TrimEntries).First();
    }

    private static byte[] StripUtf8Bom(byte[] body)
    {
        return body.Length >= 3 && body[0] == 0xEF && body[1] == 0xBB && body[2] == 0xBF
            ? body[3..]
            : body;
    }

    private static Guid? ReadGuid(IReadOnlyDictionary<string, string> headers, string key)
    {
        return headers.TryGetValue(key, out var raw) && Guid.TryParse(raw, out var value) ? value : null;
    }

    internal readonly record struct OutgoingRow(Guid Id, string Headers, byte[] Body, DateTimeOffset? Expires);
}
