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
    /// <summary>yyyy-MM-dd HH:mm:ss:ffffff Z</summary>
    private const string _dateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";
    private readonly Endpoint _endpoint;

    private readonly Lazy<Dictionary<PropertyInfo, string>> _envelopeToHeader;

    private readonly Dictionary<PropertyInfo, Action<Envelope, TOutgoing>> _envelopeToOutgoing = new();

    private readonly Dictionary<PropertyInfo, Action<Envelope, TIncoming>> _incomingToEnvelope = new();
    private readonly Lazy<Action<Envelope, TIncoming>> _mapIncoming;
    private readonly Lazy<Action<Envelope, TOutgoing>> _mapOutgoing;

    /// <summary>
    ///     Map the headers and properties of an incoming message to an Envelope.
    /// </summary>
    protected Action<Envelope, TIncoming> mapIncoming => _mapIncoming.Value;
    
    /// <summary>
    ///     Map the headers and properties of an Envelope to an outgoing message.
    /// </summary>
    protected Action<Envelope, TOutgoing> mapOutgoing => _mapOutgoing.Value;

    public EnvelopeMapper(Endpoint endpoint)
    {
        _endpoint = endpoint;

        _mapIncoming = new Lazy<Action<Envelope, TIncoming>>(compileIncoming);
        _mapOutgoing = new Lazy<Action<Envelope, TOutgoing>>(compileOutgoing);
        
        _envelopeToHeader = new Lazy<Dictionary<PropertyInfo, string>>(initializePropertiesToHeaders);
    }

    /// <summary>
    ///     Map the properties on the Envelope to headers.
    /// </summary>
    protected virtual Dictionary<PropertyInfo, string> initializePropertiesToHeaders()
    {
        return new Dictionary<PropertyInfo, string>
        {
            { getProperty(x => x.CorrelationId!), EnvelopeConstants.CorrelationIdKey },
            { getProperty(x => x.SagaId!), EnvelopeConstants.SagaIdKey },
            { getProperty(x => x.Id), EnvelopeConstants.IdKey },
            { getProperty(x => x.ConversationId), EnvelopeConstants.ConversationIdKey },
            { getProperty(x => x.ParentId!), EnvelopeConstants.ParentIdKey },
            { getProperty(x => x.ContentType!), EnvelopeConstants.ContentTypeKey },
            { getProperty(x => x.Source!), EnvelopeConstants.SourceKey },
            { getProperty(x => x.ReplyRequested!), EnvelopeConstants.ReplyRequestedKey },
            { getProperty(x => x.ReplyUri!), EnvelopeConstants.ReplyUriKey },
            { getProperty(x => x.ScheduledTime!), EnvelopeConstants.ExecutionTimeKey },
            
            { getProperty(x => x.SentAt), EnvelopeConstants.SentAtKey },
            
            { getProperty(x => x.AckRequested), EnvelopeConstants.AckRequestedKey },
            { getProperty(x => x.IsResponse), EnvelopeConstants.IsResponseKey },
            { getProperty(x => x.MessageType!), EnvelopeConstants.MessageTypeKey },
            { getProperty(x => x.AcceptedContentTypes), EnvelopeConstants.AcceptedContentTypesKey },
            
            { getProperty(x => x.TenantId!), EnvelopeConstants.TenantIdKey },
            
            { getProperty(x => x.DeliverBy!), EnvelopeConstants.DeliverByKey },
            
            { getProperty(x => x.Attempts), EnvelopeConstants.AttemptsKey }
        };
    }

    /// <summary>
    ///     Get a property from the Envelope by an expression, for use in <see cref="initializePropertiesToHeaders"/>.
    /// </summary>
    /// <param name="property">The expression to get the property from.</param>
    /// <returns>The <see cref="PropertyInfo"/> for the specified property.</returns>
    protected PropertyInfo getProperty(Expression<Func<Envelope, object>> property)
    {
        return ReflectionHelper.GetProperty(property);
    }

    /// <summary>
    ///    Get all headers that are mapped from the Envelope to the outgoing message.
    /// </summary>
    /// <returns>The header keys.</returns>
    public IEnumerable<string> AllHeaders()
    {
        return _envelopeToHeader.Value.Values;
    }

    /// <summary>
    ///     Map the incoming message to an Envelope.
    ///     By default, this will map incoming headers and properties to the Envelope
    ///     using <see cref="mapIncoming"/>, and set the serializer based on the content type of the incoming message.
    ///
    ///     If no matching serializer is found on the Endpoint, the default serializer
    ///     from the Endpoint configuration will be used.
    /// </summary>
    /// <param name="envelope">The envelope to map to.</param>
    /// <param name="incoming">The incoming message to map from.</param>
    public virtual void MapIncomingToEnvelope(Envelope envelope, TIncoming incoming)
    {
        mapIncoming(envelope, incoming);

        var contentType = envelope.ContentType;
        var serializer = _endpoint.TryFindSerializer(contentType) ?? _endpoint.DefaultSerializer;
        envelope.Serializer = serializer;
    }

    /// <summary>
    ///     Map the Envelope to an outgoing message.
    ///     By default, this will map outgoing headers and properties to the outgoing message
    ///     using <see cref="mapOutgoing"/>, and set the <see cref="TransportConstants.ProtocolVersion"/> header.
    /// </summary>
    /// <param name="envelope">The envelope to map from.</param>
    /// <param name="outgoing">The outgoing message to map to.</param>
    public virtual void MapEnvelopeToOutgoing(Envelope envelope, TOutgoing outgoing)
    {
        mapOutgoing(envelope, outgoing);
        writeOutgoingHeader(outgoing, TransportConstants.ProtocolVersion, "1.0"); // fancier later
    }

    /// <summary>
    ///     Configure the EnvelopeMapper to assume that all unidentified messages are
    ///     of the specified message type.
    /// 
    ///     This is meant primarily for interaction with incoming messages from MassTransit.
    /// </summary>
    /// <param name="messageType">The message type to assume for unidentified messages.</param>
    public void ReceivesMessage(Type messageType)
    {
        _incomingToEnvelope[ReflectionHelper.GetProperty<Envelope>(x => x.MessageType!)] =
            (e, _) => e.MessageType = messageType.ToMessageTypeName();
    }

    /// <summary>
    ///     Map a property on the Envelope with custom read and write actions.
    ///
    ///     The properties are mapped <b>after</b> the headers are written, so
    ///     you can use the headers in your custom logic if needed.
    /// </summary>
    /// <param name="property">The property on the Envelope to map.</param>
    /// <param name="readFromIncoming">The action to read the property value from the incoming message.</param>
    /// <param name="writeToOutgoing">The action to write the property value to the outgoing message.</param>
    public void MapProperty(Expression<Func<Envelope, object>> property, Action<Envelope, TIncoming> readFromIncoming,
        Action<Envelope, TOutgoing> writeToOutgoing)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _incomingToEnvelope[prop] = readFromIncoming;
        _envelopeToOutgoing[prop] = writeToOutgoing;
    }

    /// <summary>
    ///     Map a property on the Envelope to the outgoing message with a custom write action.
    ///
    ///     This is useful when you need to write complex types or perform custom logic.
    ///     The properties are mapped <b>after</b> the headers are written, so 
    ///     you can use the headers in your custom logic if needed.
    /// </summary>
    /// <param name="property">The property on the Envelope to map.</param>
    /// <param name="writeToOutgoing">The action to write the property value to the outgoing message.</param>
    public void MapOutgoingProperty(Expression<Func<Envelope, object>> property,
        Action<Envelope, TOutgoing> writeToOutgoing)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _envelopeToOutgoing[prop] = writeToOutgoing;
    }

    /// <summary>
    ///     Map a property on the Envelope to a header key.
    /// </summary>
    /// <param name="property">The property on the Envelope to map.</param>
    /// <param name="headerKey">The header key to map the property to.</param>
    public void MapPropertyToHeader(Expression<Func<Envelope, object>> property, string headerKey)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _envelopeToHeader.Value[prop] = headerKey;
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

        foreach (var pair in _envelopeToHeader.Value.Where(x => !_envelopeToOutgoing.ContainsKey(x.Key)))
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

        var headers = _envelopeToHeader.Value.Where(x => !_incomingToEnvelope.ContainsKey(x.Key));
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

    /// <summary>
    ///     Write any "other" headers from the envelope to the outgoing message.
    ///
    ///     This is specifically for custom headers that are included with messages
    ///     that are not recognized or processed by Wolverine itself.
    /// </summary>
    /// <param name="outgoing">The outgoing message to write headers to.</param>
    /// <param name="envelope">The envelope containing the headers.</param>
    protected void writeOutgoingOtherHeaders(TOutgoing outgoing, Envelope envelope)
    {
        var reserved = _envelopeToHeader.Value.Values.ToArray();

        foreach (var header in envelope.Headers.Where(x => !reserved.Contains(x.Key)))
            writeOutgoingHeader(outgoing, header.Key, header.Value!);
    }

    /// <summary>
    ///     Write a header to the outgoing message.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The value of the header to write.</param>
    protected abstract void writeOutgoingHeader(TOutgoing outgoing, string key, string value);
    
    /// <summary>
    ///     Try to read a header from the incoming message.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <param name="value">The value of the header if it exists, otherwise null.</param>
    /// <returns>A flag indicating whether the header was found.</returns>
    protected abstract bool tryReadIncomingHeader(TIncoming incoming, string key, out string? value);

    /// <summary>
    ///     Write any "other" headers from the incoming message to the envelope.
    /// 
    ///     This is specifically for custom headers that are included with messages
    ///     but are not recognized or processed by Wolverine itself.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="envelope">The envelope to write the headers to.</param>
    protected virtual void writeIncomingHeaders(TIncoming incoming, Envelope envelope)
    {
        // nothing
    }

    /// <summary>
    ///     Read a header value as a string array.
    ///     By default, this will split the header value by commas.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>The string array value of the header, or an empty array if the header is not present.</returns>
    protected virtual string[] readStringArray(TIncoming incoming, string key)
    {
        return tryReadIncomingHeader(incoming, key, out var value)
            ? value!.Split(',')
            : [];
    }

    /// <summary>
    ///     Read a header value as an integer.
    ///     By default, this will return 0 if the header is not present or cannot be parsed.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>The integer value of the header, or 0 if the header is not present or cannot be parsed.</returns>
    protected virtual int readInt(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (int.TryParse(raw, out var number))
            {
                return number;
            }
        }

        return 0;
    }

    /// <summary>
    ///     Read a header value as a string.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>The string value of the header, or null if the header is not present.</returns>
    protected virtual string? readString(TIncoming incoming, string key)
    {
        return tryReadIncomingHeader(incoming, key, out var value)
            ? value
            : null;
    }

    /// <summary>
    ///     Read a header value as a Uri.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>The Uri value of the header, or null if the header is not present or cannot be parsed.</returns>
    protected virtual Uri? readUri(TIncoming incoming, string key)
    {
        return tryReadIncomingHeader(incoming, key, out var value)
            ? new Uri(value!)
            : null;
    }

    /// <summary>
    ///     Write a string array as a header value.
    ///     By default, this will join the array elements with commas.
     ///    If the value is null, no header will be written.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The string array value to write.</param>
    protected virtual void writeStringArray(TOutgoing outgoing, string key, string[]? value)
    {
        if (value != null)
        {
            writeOutgoingHeader(outgoing, key, value.Join(","));
        }
    }

    /// <summary>
    ///     Write a Uri as a header value.
    ///     By default, this will write the header value as the string representation of the Uri.
    ///     If the value is null, no header will be written.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The Uri value to write.</param>
    protected virtual void writeUri(TOutgoing outgoing, string key, Uri? value)
    {
        if (value != null)
        {
            writeOutgoingHeader(outgoing, key, value.ToString());
        }
    }

    /// <summary>
    ///     Write a string as a header value.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The string value to write.</param>
    protected virtual void writeString(TOutgoing outgoing, string key, string? value)
    {
        if (value != null)
        {
            writeOutgoingHeader(outgoing, key, value);
        }
    }

    /// <summary>
    ///     Write an integer as a header value.
    ///     By default, this will write the header value as a string representation of the integer.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The integer value to write.</param>
    protected virtual void writeInt(TOutgoing outgoing, string key, int value)
    {
        writeOutgoingHeader(outgoing, key, value.ToString());
    }

    /// <summary>
    ///     Write a Guid as a header value.
    ///     By default, this will write the header value as a string representation of the Guid.
    ///     If the Guid is empty, no header will be written.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The Guid value to write.</param>
    protected virtual void writeGuid(TOutgoing outgoing, string key, Guid value)
    {
        if (value != Guid.Empty)
        {
            writeOutgoingHeader(outgoing, key, value.ToString());
        }
    }

    /// <summary>
    ///     Write a boolean as a header value.
    ///     By default, this will write the header value as "true" if the boolean is true.
    ///     Otherwise, no header will be written.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The boolean value to write.</param>
    protected virtual void writeBoolean(TOutgoing outgoing, string key, bool value)
    {
        if (value)
        {
            writeOutgoingHeader(outgoing, key, "true");
        }
    }

    /// <summary>
    ///     Write a DateTimeOffset as a header value.
    ///     By default, this will write the header value as a string in
    ///     the format "<inheritdoc cref="_dateTimeOffsetFormat"/>".
    ///     If the value is null, no header will be written.
    ///
    ///     Note: The DateTimeOffset is converted to UTC before writing, so <b>the offset is not preserved</b>.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The DateTimeOffset value to write.</param>
    protected virtual void writeNullableDateTimeOffset(TOutgoing outgoing, string key, DateTimeOffset? value)
    {
        if (value.HasValue)
        {
            writeOutgoingHeader(outgoing, key, value.Value.ToUniversalTime().ToString(_dateTimeOffsetFormat));
        }
    }

    /// <summary>
    ///     Write a DateTimeOffset as a header value.
    ///     By default, this will write the header value as a string in
    ///     the format "<inheritdoc cref="_dateTimeOffsetFormat"/>".
    /// 
    ///     Note: The DateTimeOffset is converted to UTC before writing, so <b>the offset is not preserved</b>.
    /// </summary>
    /// <param name="outgoing">The outgoing message.</param>
    /// <param name="key">The key of the header to write.</param>
    /// <param name="value">The DateTimeOffset value to write.</param>
    protected virtual void writeDateTimeOffset(TOutgoing outgoing, string key, DateTimeOffset value)
    {
        writeOutgoingHeader(outgoing, key, value.ToUniversalTime().ToString(_dateTimeOffsetFormat));
    }

    /// <summary>
    ///     Read a header value as a Guid.
    ///     By default, this will return <see cref="Guid.Empty"/> if the header is
    ///     not present or cannot be parsed.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>
    ///     The Guid value of the header, or <see cref="Guid.Empty"/> if the header is
    ///     not present or cannot be parsed.
    /// </returns>
    protected virtual Guid readGuid(TIncoming incoming, string key)
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

    /// <summary>
    ///     Read a header value as a boolean.
    ///     By default, this will return false if the header is not present or cannot be parsed.
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>
    ///     The boolean value of the header, or false if the header is not present or cannot be parsed.
    /// </returns>
    protected virtual bool readBoolean(TIncoming incoming, string key)
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

    /// <summary>
    ///     Read a header value as a DateTimeOffset.
    ///     By default, this will return the default value if the header is not present or
    ///     cannot be parsed from the expected format "<inheritdoc cref="_dateTimeOffsetFormat"/>".
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>
    ///     The DateTimeOffset value of the header, or default(DateTimeOffset) if
    ///     the header is not present or cannot be parsed.
    /// </returns>
    protected virtual DateTimeOffset readDateTimeOffset(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (DateTimeOffset.TryParseExact(raw, _dateTimeOffsetFormat, null, DateTimeStyles.AssumeUniversal,  out var flag))
            {
                return flag;
            }
        }

        return default;
    }

    /// <summary>
    ///     Read a header value as a nullable DateTimeOffset.
    ///     By default, this will return null if the header is not present or
    ///     cannot be parsed from the expected format "<inheritdoc cref="_dateTimeOffsetFormat"/>".
    /// </summary>
    /// <param name="incoming">The incoming message.</param>
    /// <param name="key">The key of the header to read.</param>
    /// <returns>
    ///     The nullable DateTimeOffset value of the header, or null if the header is not present or cannot be parsed.
    /// </returns>
    protected virtual DateTimeOffset? readNullableDateTimeOffset(TIncoming incoming, string key)
    {
        if (tryReadIncomingHeader(incoming, key, out var raw))
        {
            if (DateTimeOffset.TryParseExact(raw, _dateTimeOffsetFormat, null, DateTimeStyles.AssumeUniversal,
                    out var flag))
            {
                return flag;
            }
        }

        return null;
    }
}
