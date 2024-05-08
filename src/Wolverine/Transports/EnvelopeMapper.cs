using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Util;

namespace Wolverine.Transports;

public interface IOutgoingMapper<TOutgoing>
{
    void MapEnvelopeToOutgoing(Envelope envelope, TOutgoing outgoing);
}

public interface IIncomingMapper<TIncoming>
{
    void MapIncomingToEnvelope(Envelope envelope, TIncoming incoming);
    IEnumerable<string> AllHeaders();
}

public interface IEnvelopeMapper<TIncoming, TOutgoing> : IOutgoingMapper<TOutgoing>, IIncomingMapper<TIncoming>;

public abstract class EnvelopeMapper<TIncoming, TOutgoing> : IEnvelopeMapper<TIncoming, TOutgoing>
{
    private const string DateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";
    private readonly Endpoint _endpoint;

    private readonly Dictionary<PropertyInfo, string> _envelopeToHeader = new();

    private readonly Dictionary<PropertyInfo, Action<Envelope, TOutgoing>> _envelopeToOutgoing = new();

    private readonly Dictionary<PropertyInfo, Action<Envelope, TIncoming>> _incomingToEnvelope = new();
    private readonly Lazy<Action<Envelope, TIncoming>> _mapIncoming;
    private readonly Lazy<Action<Envelope, TOutgoing>> _mapOutgoing;

    public EnvelopeMapper(Endpoint endpoint)
    {
        _endpoint = endpoint;

        _mapIncoming = new Lazy<Action<Envelope, TIncoming>>(compileIncoming);
        _mapOutgoing = new Lazy<Action<Envelope, TOutgoing>>(compileOutgoing);

        MapPropertyToHeader(x => x.CorrelationId!, EnvelopeConstants.CorrelationIdKey);
        MapPropertyToHeader(x => x.SagaId!, EnvelopeConstants.SagaIdKey);
        MapPropertyToHeader(x => x.Id, EnvelopeConstants.IdKey);
        MapPropertyToHeader(x => x.ConversationId, EnvelopeConstants.ConversationIdKey);
        MapPropertyToHeader(x => x.ParentId!, EnvelopeConstants.ParentIdKey);
        MapPropertyToHeader(x => x.ContentType!, EnvelopeConstants.ContentTypeKey);
        MapPropertyToHeader(x => x.Source!, EnvelopeConstants.SourceKey);
        MapPropertyToHeader(x => x.ReplyRequested!, EnvelopeConstants.ReplyRequestedKey);
        MapPropertyToHeader(x => x.ReplyUri!, EnvelopeConstants.ReplyUriKey);
        MapPropertyToHeader(x => x.ScheduledTime!, EnvelopeConstants.ExecutionTimeKey);

        MapPropertyToHeader(x => x.SentAt, EnvelopeConstants.SentAtKey);

        MapPropertyToHeader(x => x.AckRequested, EnvelopeConstants.AckRequestedKey);
        MapPropertyToHeader(x => x.IsResponse, EnvelopeConstants.IsResponseKey);
        MapPropertyToHeader(x => x.MessageType!, EnvelopeConstants.MessageTypeKey);
        MapPropertyToHeader(x => x.AcceptedContentTypes, EnvelopeConstants.AcceptedContentTypesKey);

        MapPropertyToHeader(x => x.TenantId!, EnvelopeConstants.TenantIdKey);

        MapPropertyToHeader(x => x.DeliverBy!, EnvelopeConstants.DeliverByKey);

        MapPropertyToHeader(x => x.Attempts, EnvelopeConstants.AttemptsKey);
    }

    public IEnumerable<string> AllHeaders()
    {
        return _envelopeToHeader.Values;
    }

    public void MapIncomingToEnvelope(Envelope envelope, TIncoming incoming)
    {
        _mapIncoming.Value(envelope, incoming);

        var contentType = envelope.ContentType;
        var serializer = _endpoint.TryFindSerializer(contentType) ?? _endpoint.DefaultSerializer;
        envelope.Serializer = serializer;
    }

    public void MapEnvelopeToOutgoing(Envelope envelope, TOutgoing outgoing)
    {

        _mapOutgoing.Value(envelope, outgoing);
        writeOutgoingHeader(outgoing, TransportConstants.ProtocolVersion, "1.0"); // fancier later
    }

    /// <summary>
    ///     This endpoint will assume that any unidentified incoming message types
    ///     are the supplied message type. This is meant primarily for interaction
    ///     with incoming messages from MassTransit
    /// </summary>
    /// <param name="messageType"></param>
    public void ReceivesMessage(Type messageType)
    {
        _incomingToEnvelope[ReflectionHelper.GetProperty<Envelope>(x => x.MessageType!)] =
            (e, _) => e.MessageType = messageType.ToMessageTypeName();
    }

    public void MapProperty(Expression<Func<Envelope, object>> property, Action<Envelope, TIncoming> readFromIncoming,
        Action<Envelope, TOutgoing> writeToOutgoing)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _incomingToEnvelope[prop] = readFromIncoming;
        _envelopeToOutgoing[prop] = writeToOutgoing;
    }

    public void MapOutgoingProperty(Expression<Func<Envelope, object>> property,
        Action<Envelope, TOutgoing> writeToOutgoing)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _envelopeToOutgoing[prop] = writeToOutgoing;
    }

    public void MapPropertyToHeader(Expression<Func<Envelope, object>> property, string headerKey)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _envelopeToHeader[prop] = headerKey;
    }

    private Action<Envelope, TIncoming> compileIncoming()
    {
        var incoming = Expression.Parameter(typeof(TIncoming), "incoming");
        var envelope = Expression.Parameter(typeof(Envelope), "env");
        var protocol = Expression.Constant(this);

        var getUri = GetType().GetMethod(nameof(readUri), BindingFlags.NonPublic | BindingFlags.Instance);
        var getInt = GetType().GetMethod(nameof(readInt), BindingFlags.NonPublic | BindingFlags.Instance);
        var getString = GetType().GetMethod(nameof(readString), BindingFlags.NonPublic | BindingFlags.Instance);
        var getGuid = GetType().GetMethod(nameof(readGuid), BindingFlags.NonPublic | BindingFlags.Instance);
        var getBoolean = GetType().GetMethod(nameof(readBoolean), BindingFlags.NonPublic | BindingFlags.Instance);
        var getNullableDateTimeOffset =
            GetType().GetMethod(nameof(readNullableDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var getDateTimeOffset =
            GetType().GetMethod(nameof(readDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var getStringArray =
            GetType().GetMethod(nameof(readStringArray), BindingFlags.NonPublic | BindingFlags.Instance);

        var writeHeaders = Expression.Call(protocol,
            GetType().GetMethod(nameof(writeIncomingHeaders), BindingFlags.NonPublic | BindingFlags.Instance)!,
            incoming, envelope);

        var list = new List<Expression>
        {
            writeHeaders
        };

        foreach (var pair in _envelopeToHeader.Where(x => !_envelopeToOutgoing.ContainsKey(x.Key)))
        {
            var getMethod = getString!;
            if (pair.Key.PropertyType == typeof(Uri))
            {
                getMethod = getUri!;
            }
            else if (pair.Key.PropertyType == typeof(Guid))
            {
                getMethod = getGuid!;
            }
            else if (pair.Key.PropertyType == typeof(bool))
            {
                getMethod = getBoolean!;
            }
            else if (pair.Key.PropertyType == typeof(DateTimeOffset))
            {
                getMethod = getDateTimeOffset!;
            }
            else if (pair.Key.PropertyType == typeof(DateTimeOffset?))
            {
                getMethod = getNullableDateTimeOffset!;
            }
            else if (pair.Key.PropertyType == typeof(int))
            {
                getMethod = getInt!;
            }
            else if (pair.Key.PropertyType == typeof(string[]))
            {
                getMethod = getStringArray!;
            }

            var setter = pair.Key.SetMethod;

            var getValue = Expression.Call(protocol, getMethod, incoming, Expression.Constant(pair.Value));
            var setValue = Expression.Call(envelope, setter!, getValue);

            list.Add(setValue);
        }

        foreach (var pair in _incomingToEnvelope)
        {
            var constant = Expression.Constant(pair.Value);
            var method = typeof(Action<Envelope, TIncoming>).GetMethod(nameof(Action.Invoke));

            var invoke = Expression.Call(constant, method!, envelope, incoming);
            list.Add(invoke);
        }

        var block = Expression.Block(list);

        var lambda = Expression.Lambda<Action<Envelope, TIncoming>>(block, envelope, incoming);

        return lambda.CompileFast();
    }

    private Action<Envelope, TOutgoing> compileOutgoing()
    {
        var outgoing = Expression.Parameter(typeof(TOutgoing), "outgoing");
        var envelope = Expression.Parameter(typeof(Envelope), "env");
        var protocol = Expression.Constant(this);

        var setUri = GetType().GetMethod(nameof(writeUri), BindingFlags.NonPublic | BindingFlags.Instance);
        var setInt = GetType().GetMethod(nameof(writeInt), BindingFlags.NonPublic | BindingFlags.Instance);
        var setString = GetType().GetMethod(nameof(writeString), BindingFlags.NonPublic | BindingFlags.Instance);
        var setGuid = GetType().GetMethod(nameof(writeGuid), BindingFlags.NonPublic | BindingFlags.Instance);
        var setBoolean = GetType().GetMethod(nameof(writeBoolean), BindingFlags.NonPublic | BindingFlags.Instance);
        var setNullableDateTimeOffset =
            GetType().GetMethod(nameof(writeNullableDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var setDateTimeOffset =
            GetType().GetMethod(nameof(writeDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var setStringArray =
            GetType().GetMethod(nameof(writeStringArray), BindingFlags.NonPublic | BindingFlags.Instance);

        var writeHeaders = Expression.Call(protocol,
            GetType().GetMethod(nameof(writeOutgoingOtherHeaders), BindingFlags.NonPublic | BindingFlags.Instance)!,
            outgoing, envelope);

        var list = new List<Expression>
        {
            writeHeaders
        };

        var headers = _envelopeToHeader.Where(x => !_incomingToEnvelope.ContainsKey(x.Key));
        foreach (var pair in headers)
        {
            var setMethod = setString!;
            if (pair.Key.PropertyType == typeof(Uri))
            {
                setMethod = setUri!;
            }
            else if (pair.Key.PropertyType == typeof(Guid))
            {
                setMethod = setGuid!;
            }
            else if (pair.Key.PropertyType == typeof(bool))
            {
                setMethod = setBoolean!;
            }
            else if (pair.Key.PropertyType == typeof(DateTimeOffset))
            {
                setMethod = setDateTimeOffset!;
            }
            else if (pair.Key.PropertyType == typeof(DateTimeOffset?))
            {
                setMethod = setNullableDateTimeOffset!;
            }
            else if (pair.Key.PropertyType == typeof(int))
            {
                setMethod = setInt!;
            }
            else if (pair.Key.PropertyType == typeof(string[]))
            {
                setMethod = setStringArray!;
            }

            var getEnvelopeValue = Expression.Call(envelope, pair.Key.GetMethod!);
            var setOutgoingValue = Expression.Call(protocol, setMethod, outgoing, Expression.Constant(pair.Value),
                getEnvelopeValue);

            list.Add(setOutgoingValue);
        }

        foreach (var pair in _envelopeToOutgoing)
        {
            var constant = Expression.Constant(pair.Value);
            var method = typeof(Action<Envelope, TOutgoing>).GetMethod(nameof(Action.Invoke));

            var invoke = Expression.Call(constant, method!, envelope, outgoing);
            list.Add(invoke);
        }

        var block = Expression.Block(list);

        var lambda = Expression.Lambda<Action<Envelope, TOutgoing>>(block, envelope, outgoing);

        return lambda.CompileFast();
    }

    protected void writeOutgoingOtherHeaders(TOutgoing outgoing, Envelope envelope)
    {
        var reserved = _envelopeToHeader.Values.ToArray();

        foreach (var header in envelope.Headers.Where(x => !reserved.Contains(x.Key)))
            writeOutgoingHeader(outgoing, header.Key, header.Value!);
    }

    protected abstract void writeOutgoingHeader(TOutgoing outgoing, string key, string value);
    protected abstract bool tryReadIncomingHeader(TIncoming incoming, string key, out string? value);

    /// <summary>
    ///     This is strictly for "other" headers that are passed along that are not
    ///     used by Wolverine
    /// </summary>
    /// <param name="incoming"></param>
    /// <param name="envelope"></param>
    protected virtual void writeIncomingHeaders(TIncoming incoming, Envelope envelope)
    {
        // nothing
    }

    protected string[] readStringArray(TIncoming incoming, string key)
    {
        return tryReadIncomingHeader(incoming, key, out var value)
            ? value!.Split(',')
            : [];
    }

    protected int readInt(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (int.TryParse(raw, out var number))
            {
                return number;
            }
        }

        return default;
    }

    protected string? readString(TIncoming incoming, string key)
    {
        return tryReadIncomingHeader(incoming, key, out var value)
            ? value
            : null;
    }

    protected Uri? readUri(TIncoming incoming, string key)
    {
        return tryReadIncomingHeader(incoming, key, out var value)
            ? new Uri(value!)
            : null;
    }

    protected void writeStringArray(TOutgoing outgoing, string key, string[]? value)
    {
        if (value != null)
        {
            writeOutgoingHeader(outgoing, key, value.Join(","));
        }
    }

    protected void writeUri(TOutgoing outgoing, string key, Uri? value)
    {
        if (value != null)
        {
            writeOutgoingHeader(outgoing, key, value.ToString());
        }
    }

    protected void writeString(TOutgoing outgoing, string key, string? value)
    {
        if (value != null)
        {
            writeOutgoingHeader(outgoing, key, value);
        }
    }

    protected void writeInt(TOutgoing outgoing, string key, int value)
    {
        writeOutgoingHeader(outgoing, key, value.ToString());
    }

    protected void writeGuid(TOutgoing outgoing, string key, Guid value)
    {
        if (value != Guid.Empty)
        {
            writeOutgoingHeader(outgoing, key, value.ToString());
        }
    }

    protected void writeBoolean(TOutgoing outgoing, string key, bool value)
    {
        if (value)
        {
            writeOutgoingHeader(outgoing, key, "true");
        }
    }

    protected void writeNullableDateTimeOffset(TOutgoing outgoing, string key, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            writeOutgoingHeader(outgoing, key, value.Value.ToUniversalTime().ToString(DateTimeOffsetFormat));
        }
    }

    protected void writeDateTimeOffset(TOutgoing outgoing, string key, DateTimeOffset value)
    {
        writeOutgoingHeader(outgoing, key, value.ToUniversalTime().ToString(DateTimeOffsetFormat));
    }

    protected Guid readGuid(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (Guid.TryParse(raw, out var uuid))
            {
                return uuid;
            }
        }

        return Guid.Empty;
    }

    protected bool readBoolean(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (bool.TryParse(raw, out var flag))
            {
                return flag;
            }
        }

        return false;
    }

    protected DateTimeOffset readDateTimeOffset(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (DateTimeOffset.TryParse(raw, out var flag))
            {
                return flag;
            }
        }

        return default;
    }

    protected DateTimeOffset? readNullableDateTimeOffset(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (DateTimeOffset.TryParseExact(raw, DateTimeOffsetFormat, null, DateTimeStyles.AssumeUniversal,
                    out var flag))
            {
                return flag;
            }
        }

        return null;
    }
}