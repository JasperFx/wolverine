using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Util;
using Endpoint = Wolverine.Configuration.Endpoint;

namespace Wolverine.Postgresql.Transport.MassTransit;

[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(MassTransitPostgresqlHostInfo))]
internal partial class MassTransitPostgresqlJsonContext : JsonSerializerContext;

/// <summary>
/// The MassTransit "host" object stored in the <c>host</c> jsonb column of a MassTransit
/// message. Only the small set of fields MassTransit populates for a host descriptor.
/// </summary>
internal sealed class MassTransitPostgresqlHostInfo
{
    public string? MachineName { get; set; }
    public string? ProcessName { get; set; }
    public int ProcessId { get; set; }
    public string? Assembly { get; set; }
    public string? AssemblyVersion { get; set; }
    public string? FrameworkVersion { get; set; }
    public string? MassTransitVersion { get; set; }
    public string? OperatingSystemVersion { get; set; }
}

/// <summary>
/// Translates between Wolverine <see cref="Envelope"/>s and the MassTransit PostgreSQL transport
/// message model. MassTransit stores the BARE message contract JSON in the <c>body</c> column
/// (content type <c>application/json</c>) with the envelope metadata in dedicated columns, and
/// keys handler dispatch off a <c>urn:message:{Namespace}:{TypeName}</c> message type.
/// </summary>
internal class MassTransitPostgresqlEnvelopeMapper
{
    private const string JsonContentType = "application/json";

    private readonly Endpoint _endpoint;
    private readonly MassTransitPostgresqlTransport _transport;
    private readonly Func<string?> _replyQueueName;

    public MassTransitPostgresqlEnvelopeMapper(MassTransitPostgresqlQueue queue, Func<string?> replyQueueName)
    {
        _endpoint = queue;
        _transport = queue.Parent;
        _replyQueueName = replyQueueName;
    }

    /// <summary>
    /// Build the parameter values passed to <c>transport.send_message</c> for an outgoing envelope.
    /// </summary>
    public OutgoingMessage MapOutgoing(MassTransitPostgresqlQueue destination, Envelope envelope)
    {
        var serializer = envelope.Serializer ?? _endpoint.DefaultSerializer
            ?? throw new InvalidOperationException("No serializer is configured for this endpoint");

        var bodyBytes = envelope.Data ?? serializer.WriteMessage(envelope.Message!);
        var body = Encoding.UTF8.GetString(bodyBytes);

        var messageType = envelope.Message?.GetType();
        var messageTypeUrn = messageType != null
            ? BuildMessageTypeUrn(messageType)
            : envelope.MessageType;

        var headers = new Dictionary<string, string>();
        foreach (var pair in envelope.Headers)
        {
            if (pair.Value != null)
            {
                headers[pair.Key] = pair.Value;
            }
        }

        var headersJson = JsonSerializer.Serialize(headers,
            MassTransitPostgresqlJsonContext.Default.DictionaryStringString);

        var hostJson = JsonSerializer.Serialize(BuildHostInfo(),
            MassTransitPostgresqlJsonContext.Default.MassTransitPostgresqlHostInfo);

        var destinationAddress = BuildDbUri(destination.Name);

        string? responseAddress = null;
        var replyName = _replyQueueName();
        if (replyName.IsNotEmpty())
        {
            responseAddress = BuildDbUri(replyName!);
        }

        return new OutgoingMessage
        {
            EntityName = destination.Name,
            Body = body,
            ContentType = JsonContentType,
            MessageType = messageTypeUrn,
            MessageId = envelope.Id,
            CorrelationId = parseGuid(envelope.CorrelationId),
            ConversationId = envelope.ConversationId == Guid.Empty ? null : envelope.ConversationId,
            SourceAddress = responseAddress,
            DestinationAddress = destinationAddress,
            ResponseAddress = responseAddress,
            Headers = headersJson,
            Host = hostJson
        };
    }

    /// <summary>
    /// Hydrate an <see cref="Envelope"/> from a <c>transport.fetch_messages</c> result row.
    /// </summary>
    public Envelope MapIncoming(FetchedRow row)
    {
        var envelope = new Envelope
        {
            Data = row.Body.IsNotEmpty() ? Encoding.UTF8.GetBytes(row.Body!) : [],
            ContentType = JsonContentType
        };

        envelope.Id = row.MessageId ?? Guid.NewGuid();

        if (row.ConversationId.HasValue)
        {
            envelope.ConversationId = row.ConversationId.Value;
        }

        if (row.CorrelationId.HasValue)
        {
            envelope.CorrelationId = row.CorrelationId.Value.ToString();
        }

        if (row.MessageType.IsNotEmpty())
        {
            envelope.MessageType = ResolveMessageType(row.MessageType!);
        }

        // Mirror MassTransitJsonSerializer: reply to the response address, else the source address.
        var replyAddress = row.ResponseAddress.IsNotEmpty() ? row.ResponseAddress : row.SourceAddress;
        if (replyAddress.IsNotEmpty())
        {
            var translated = TranslateDbUriToWolverine(replyAddress!);
            if (translated != null)
            {
                envelope.ReplyUri = translated;
            }
        }

        if (row.SentTime.HasValue)
        {
            envelope.SentAt = new DateTimeOffset(row.SentTime.Value.ToUniversalTime(), TimeSpan.Zero);
        }

        if (row.Headers.IsNotEmpty())
        {
            var headers = JsonSerializer.Deserialize(row.Headers!,
                MassTransitPostgresqlJsonContext.Default.DictionaryStringString);
            if (headers != null)
            {
                foreach (var pair in headers)
                {
                    envelope.Headers[pair.Key] = pair.Value;
                }
            }
        }

        envelope.Serializer = _endpoint.TryFindSerializer(JsonContentType) ?? _endpoint.DefaultSerializer;

        return envelope;
    }

    /// <summary>
    /// MassTransit keys dispatch off <c>urn:message:{Namespace}:{TypeName}</c>; mirror the
    /// existing <c>MassTransitEnvelope</c> shape (NameInCode for generic-aware naming).
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification =
            "Reflecting over a live message instance's runtime type to build the MassTransit urn; the type is present at runtime.")]
    internal static string BuildMessageTypeUrn(Type messageType)
    {
        return $"urn:message:{messageType.Namespace}:{messageType.NameInCode()}";
    }

    /// <summary>
    /// Resolve a MassTransit <c>urn:message:Ns:Type</c> back to a Wolverine message type name.
    /// The last ':'-separated segment is the type name; the remainder (joined by '.') is the
    /// namespace. If the resulting type can be loaded use <see cref="TypeExtensions.ToMessageTypeName"/>,
    /// otherwise fall back to the bare type name.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2057",
        Justification =
            "The message type name comes from a foreign MassTransit endpoint at runtime; a failed resolution falls back to the full type name which Wolverine binds via RegisterInteropMessageAssembly.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification =
            "Resolving an interop message type by name across loaded assemblies; the assembly is registered via RegisterInteropMessageAssembly and a miss falls back to the full type name.")]
    internal static string ResolveMessageType(string messageType)
    {
        // MassTransit may carry multiple type entries separated by ';' (concrete + interfaces).
        var first = messageType.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? messageType;

        const string prefix = "urn:message:";
        var bare = first.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? first[prefix.Length..]
            : first;

        var lastColon = bare.LastIndexOf(':');
        if (lastColon <= 0 || lastColon == bare.Length - 1)
        {
            return bare;
        }

        var typeName = bare[(lastColon + 1)..];
        var ns = bare[..lastColon].Replace(':', '.');
        var fullName = $"{ns}.{typeName}";

        // The urn carries no assembly, so Type.GetType(fullName) usually fails; search the loaded
        // assemblies (the interop message assembly is registered via RegisterInteropMessageAssembly).
        var type = Type.GetType(fullName);
        if (type == null)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                type = assembly.GetType(fullName);
                if (type != null) break;
            }
        }

        // Fall back to the full "Namespace.TypeName" (NOT the bare type name) so it matches the
        // Wolverine message-type-name the handler is registered under.
        return type != null ? type.ToMessageTypeName() : fullName;
    }

    /// <summary>
    /// Build a MassTransit <c>db://{host}:{port}/{queue}</c> address.
    /// </summary>
    internal string BuildDbUri(string queueName)
    {
        return $"db://{_transport.MassTransitHost}/{queueName}";
    }

    /// <summary>
    /// Translate a MassTransit <c>db://...</c> response address to this transport's
    /// <c>masstransit-postgresql://{queue}</c> reply URI by taking the last path segment.
    /// </summary>
    internal static Uri? TranslateDbUriToWolverine(string dbUri)
    {
        if (Uri.TryCreate(dbUri, UriKind.Absolute, out var uri))
        {
            var lastSegment = uri.Segments.LastOrDefault()?.Trim('/');
            if (lastSegment.IsNotEmpty())
            {
                return MassTransitPostgresqlQueue.ToUri(lastSegment!);
            }
        }
        else
        {
            // Best-effort: take the substring after the last '/'.
            var slash = dbUri.LastIndexOf('/');
            if (slash >= 0 && slash < dbUri.Length - 1)
            {
                return MassTransitPostgresqlQueue.ToUri(dbUri[(slash + 1)..]);
            }
        }

        return null;
    }

    private static MassTransitPostgresqlHostInfo BuildHostInfo()
    {
        var instance = Wolverine.Runtime.Interop.MassTransit.BusHostInfo.Instance;
        return new MassTransitPostgresqlHostInfo
        {
            MachineName = instance.MachineName,
            ProcessName = instance.ProcessName,
            ProcessId = instance.ProcessId,
            Assembly = instance.Assembly,
            AssemblyVersion = instance.AssemblyVersion,
            FrameworkVersion = instance.FrameworkVersion,
            MassTransitVersion = instance.MassTransitVersion,
            OperatingSystemVersion = instance.OperatingSystemVersion
        };
    }

    private static Guid? parseGuid(string? value)
    {
        return value.IsNotEmpty() && Guid.TryParse(value, out var guid) ? guid : null;
    }

    /// <summary>
    /// Carries the values bound into the <c>transport.send_message</c> call for an outgoing
    /// message. Nullable Guid/string values are sent as typed NULLs by the sender.
    /// </summary>
    internal sealed class OutgoingMessage
    {
        public string EntityName { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public string ContentType { get; init; } = JsonContentType;
        public string? MessageType { get; init; }
        public Guid MessageId { get; init; }
        public Guid? CorrelationId { get; init; }
        public Guid? ConversationId { get; init; }
        public string? SourceAddress { get; init; }
        public string? DestinationAddress { get; init; }
        public string? ResponseAddress { get; init; }
        public string Headers { get; init; } = "{}";
        public string Host { get; init; } = "{}";
    }

    /// <summary>
    /// One row read back from <c>transport.fetch_messages</c>, including the lease coordinates
    /// (<see cref="MessageDeliveryId"/> / <see cref="LockId"/>) needed for ack/nack.
    /// </summary>
    internal sealed class FetchedRow
    {
        public long MessageDeliveryId { get; init; }
        public Guid LockId { get; init; }
        public string? ContentType { get; init; }
        public string? MessageType { get; init; }
        public string? Body { get; init; }
        public Guid? MessageId { get; init; }
        public Guid? CorrelationId { get; init; }
        public Guid? ConversationId { get; init; }
        public string? SourceAddress { get; init; }
        public string? DestinationAddress { get; init; }
        public string? ResponseAddress { get; init; }
        public DateTime? SentTime { get; init; }
        public string? Headers { get; init; }
    }
}
