using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using FastExpressionCompiler;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Runtime.Interop.MassTransit;
using Wolverine.Util;

namespace Wolverine.Transports;

public interface IOutgoingMapper<TOutgoing>
{
    public void MapEnvelopeToOutgoing(Envelope envelope, TOutgoing outgoing);
}

public interface IIncomingMapper<TIncoming>
{
    public void MapIncomingToEnvelope(Envelope envelope, TIncoming incoming);
}

public interface IEnvelopeMapper<TIncoming, TOutgoing> : IOutgoingMapper<TOutgoing>, IIncomingMapper<TIncoming>;

public interface IEnvelopeMapper
{
    /// <summary>
    ///     This endpoint will assume that any unidentified incoming message types
    ///     are the supplied message type. This is meant primarily for interaction
    ///     with incoming messages from MassTransit
    /// </summary>
    /// <param name="messageType"></param>
    void ReceivesMessage(Type messageType);

    /// <summary>
    /// Declaratively map a header value to 
    /// </summary>
    /// <param name="property"></param>
    /// <param name="headerKey"></param>
    void MapPropertyToHeader(Expression<Func<Envelope, object>> property, string headerKey);
}

public abstract class EnvelopeMapper<TIncoming, TOutgoing> : IEnvelopeMapper<TIncoming, TOutgoing>, IEnvelopeMapper
{
    private const string DateTimeOffsetFormat = "yyyy-MM-dd HH:mm:ss:ffffff Z";
    private readonly Endpoint _endpoint;

    private readonly Dictionary<PropertyInfo, string> _envelopeToHeader = new();

    private readonly Dictionary<PropertyInfo, Action<Envelope, TOutgoing>> _envelopeToOutgoing = new();

    private readonly Dictionary<PropertyInfo, Action<Envelope, TIncoming>> _incomingToEnvelope = new();
    private readonly Lazy<Action<Envelope, TIncoming>> _mapIncoming;
    private readonly Lazy<Action<Envelope, TOutgoing>> _mapOutgoing;

    /// <summary>
    /// Returns this mapper's runtime type for the reflective lookups in
    /// <see cref="compileIncoming"/> and <see cref="compileOutgoing"/>.
    /// Annotated with <see cref="DynamicallyAccessedMemberTypes.NonPublicMethods"/>
    /// so the trim analyzer knows the non-public read*/write* helpers
    /// resolved by <c>GetMethod(nameof(...), NonPublic | Instance)</c> must
    /// be preserved.
    /// </summary>
    /// <remarks>
    /// All the names looked up are <c>nameof()</c> literals against concrete
    /// methods declared on this class, so the only thing the trimmer can
    /// reasonably remove is the methods themselves — annotating the return
    /// type tells it not to.
    /// </remarks>
    [return: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicMethods)]
    [UnconditionalSuppressMessage("Trimming", "IL2073",
        Justification = "GetType() returns the concrete EnvelopeMapper subclass at runtime; " +
                        "the read*/write* methods looked up reflectively are non-public instance " +
                        "members declared on this class hierarchy and reached via nameof() literals " +
                        "from compileIncoming/compileOutgoing, both of which are RequiresUnreferencedCode-annotated. " +
                        "The trimmer is told to preserve those methods via the return-DAM annotation; " +
                        "this suppression bridges the GetType-returns-unannotated-Type gap.")]
    private Type getMapperTypeForReflection() => GetType();

    [RequiresUnreferencedCode(
        "EnvelopeMapper compiles per-property header read/write expressions via FastExpressionCompiler. " +
        "Trimming may remove the GetType().GetMethod-resolved read*/write* helpers used by the compiled " +
        "expressions, breaking incoming/outgoing header mapping. Static-mode apps that pre-generate transport " +
        "code via JasperFx codegen avoid this path. See the Wolverine AOT publishing guide.")]
    [RequiresDynamicCode(
        "EnvelopeMapper uses FastExpressionCompiler to JIT-compile the per-property header reader/writer " +
        "delegates. Native AOT cannot execute Expression.Compile at runtime. Static-mode apps that pre-generate " +
        "transport code avoid this path. See the Wolverine AOT publishing guide.")]
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
        MapPropertyToHeader(x => x.UserName!, EnvelopeConstants.UserNameKey);

        MapPropertyToHeader(x => x.DeliverBy!, EnvelopeConstants.DeliverByKey);

        MapPropertyToHeader(x => x.Attempts, EnvelopeConstants.AttemptsKey);
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

    public void InteropWithMassTransit(Action<IMassTransitInterop>? configure = null)
    {
        if (_endpoint is IMassTransitInteropEndpoint e)
        {
            var serializer = new MassTransitJsonSerializer(e);
            configure?.Invoke(serializer);

            MapPropertyToHeader(x => x.MessageType!, MassTransitHeaders.MessageType);
            MapPropertyToHeader(x => x.ParentId!, MassTransitHeaders.ActivityId);

            _endpoint.DefaultSerializer = serializer;
            
            var replyUri = new Lazy<string>(() => e.MassTransitReplyUri()?.ToString() ?? string.Empty);
            
            MapOutgoingProperty(x => x.ReplyUri!, (envelope, outgoing) =>
            {
                writeOutgoingHeader(outgoing, MassTransitHeaders.ResponseAddress, replyUri.Value);
            });
        }
        else
        {
            throw new NotSupportedException($"Endpoint of {_endpoint} does not (yet) support interoperability with MassTransit");
        }

    }

    public void MapProperty(Expression<Func<Envelope, object>> property, Action<Envelope, TIncoming> readFromIncoming,
        Action<Envelope, TOutgoing> writeToOutgoing)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _incomingToEnvelope[prop] = readFromIncoming;
        _envelopeToOutgoing[prop] = writeToOutgoing;
    }

    public void MapIncomingProperty(Expression<Func<Envelope, object>> property,
        Action<Envelope, TIncoming> readFromIncoming)
    {
        var prop = ReflectionHelper.GetProperty(property);
        _incomingToEnvelope[prop] = readFromIncoming;
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

    [RequiresUnreferencedCode("Compiles per-property expression-tree readers. See EnvelopeMapper constructor doc.")]
    [RequiresDynamicCode("FastExpressionCompiler.CompileFast() emits IL at runtime. See EnvelopeMapper constructor doc.")]
    private Action<Envelope, TIncoming> compileIncoming()
    {
        var incoming = Expression.Parameter(typeof(TIncoming), "incoming");
        var envelope = Expression.Parameter(typeof(Envelope), "env");
        var protocol = Expression.Constant(this);

        var mapperType = getMapperTypeForReflection();
        var getUri = mapperType.GetMethod(nameof(readUri), BindingFlags.NonPublic | BindingFlags.Instance);
        var getInt = mapperType.GetMethod(nameof(readInt), BindingFlags.NonPublic | BindingFlags.Instance);
        var getString = mapperType.GetMethod(nameof(readString), BindingFlags.NonPublic | BindingFlags.Instance);
        var getGuid = mapperType.GetMethod(nameof(readGuid), BindingFlags.NonPublic | BindingFlags.Instance);
        var getBoolean = mapperType.GetMethod(nameof(readBoolean), BindingFlags.NonPublic | BindingFlags.Instance);
        var getNullableDateTimeOffset =
            mapperType.GetMethod(nameof(readNullableDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var getDateTimeOffset =
            mapperType.GetMethod(nameof(readDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var getStringArray =
            mapperType.GetMethod(nameof(readStringArray), BindingFlags.NonPublic | BindingFlags.Instance);

        var writeHeaders = Expression.Call(protocol,
            mapperType.GetMethod(nameof(writeIncomingHeaders), BindingFlags.NonPublic | BindingFlags.Instance)!,
            incoming, envelope);

        var list = new List<Expression>
        {
            writeHeaders
        };

        // Use the default header read for a property unless the caller has
        // supplied a custom incoming mapping for it. Previously this predicate
        // was accidentally checking _envelopeToOutgoing, which caused
        // MapOutgoingProperty to silently delete the incoming header read for
        // the same property. See https://github.com/JasperFx/wolverine/issues/2551.
        foreach (var pair in _envelopeToHeader.Where(x => !_incomingToEnvelope.ContainsKey(x.Key)))
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

    [RequiresUnreferencedCode("Compiles per-property expression-tree writers. See EnvelopeMapper constructor doc.")]
    [RequiresDynamicCode("FastExpressionCompiler.CompileFast() emits IL at runtime. See EnvelopeMapper constructor doc.")]
    private Action<Envelope, TOutgoing> compileOutgoing()
    {
        var outgoing = Expression.Parameter(typeof(TOutgoing), "outgoing");
        var envelope = Expression.Parameter(typeof(Envelope), "env");
        var protocol = Expression.Constant(this);

        var mapperType = getMapperTypeForReflection();
        var setUri = mapperType.GetMethod(nameof(writeUri), BindingFlags.NonPublic | BindingFlags.Instance);
        var setInt = mapperType.GetMethod(nameof(writeInt), BindingFlags.NonPublic | BindingFlags.Instance);
        var setString = mapperType.GetMethod(nameof(writeString), BindingFlags.NonPublic | BindingFlags.Instance);
        var setGuid = mapperType.GetMethod(nameof(writeGuid), BindingFlags.NonPublic | BindingFlags.Instance);
        var setBoolean = mapperType.GetMethod(nameof(writeBoolean), BindingFlags.NonPublic | BindingFlags.Instance);
        var setNullableDateTimeOffset =
            mapperType.GetMethod(nameof(writeNullableDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var setDateTimeOffset =
            mapperType.GetMethod(nameof(writeDateTimeOffset), BindingFlags.NonPublic | BindingFlags.Instance);
        var setStringArray =
            mapperType.GetMethod(nameof(writeStringArray), BindingFlags.NonPublic | BindingFlags.Instance);

        var writeHeaders = Expression.Call(protocol,
            mapperType.GetMethod(nameof(writeOutgoingOtherHeaders), BindingFlags.NonPublic | BindingFlags.Instance)!,
            outgoing, envelope);

        var list = new List<Expression>
        {
            writeHeaders
        };

        // Use the default header write for a property unless the caller has
        // supplied a custom outgoing mapping for it. Previously this predicate
        // was accidentally checking _incomingToEnvelope, which caused
        // MapIncomingProperty to silently delete the outgoing header write for
        // the same property. See https://github.com/JasperFx/wolverine/issues/2551.
        var headers = _envelopeToHeader.Where(x => !_envelopeToOutgoing.ContainsKey(x.Key));
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
            if (DateTimeOffset.TryParseExact(raw, DateTimeOffsetFormat, null, DateTimeStyles.AssumeUniversal,  out var flag))
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