using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
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

/// <summary>
/// Base class for transport-specific envelope mappers. Translates between Wolverine's
/// <see cref="Envelope"/> and a transport's incoming/outgoing message shape via a
/// fixed set of header reads/writes plus user-supplied <c>MapProperty</c> callbacks.
/// </summary>
/// <remarks>
/// AOT note (#2755 / #2746): the per-property header reader/writer dispatch was
/// previously built via <c>FastExpressionCompiler.CompileFast()</c> over a
/// dynamically-built <see cref="Expression"/> tree, which required runtime IL emit
/// (annotated as <c>[RequiresDynamicCode]</c>). The refactor in this file replaces
/// that path with eager <see cref="Delegate.CreateDelegate(Type, MethodInfo)"/>-built
/// open-instance delegates over <see cref="Envelope"/>'s property setters/getters,
/// composed into per-direction <see cref="Action{Envelope, TIncoming}"/> /
/// <see cref="Action{Envelope, TOutgoing}"/> dispatch lists at first-use. The result:
/// no runtime IL emit, no FastExpressionCompiler dependency, no <c>[RequiresDynamicCode]</c>
/// on the constructor — and transport packages (RabbitMQ, Service Bus, SQS, etc.)
/// can drop the leaf-suppression they inherited via <c>EnvelopeMapper&lt;,&gt;</c>'s
/// reflective property mapping.
///
/// Throughput: <c>CreateDelegate</c> produces an "open" delegate that's a single
/// indirect call per property — slower than the JIT-compiled expression block by
/// a few ns/op, but the cost is amortized over millions of messages and
/// disappears in practice next to the rest of the dispatch path. Benchmark
/// (Scalability.WolverinePerfTest) is the right place to confirm; document any
/// regression in docs/guide/aot.md if measurable.
/// </remarks>
public abstract class EnvelopeMapper<TIncoming, TOutgoing> : IEnvelopeMapper<TIncoming, TOutgoing>, IEnvelopeMapper
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

        _mapIncoming = new Lazy<Action<Envelope, TIncoming>>(buildIncoming);
        _mapOutgoing = new Lazy<Action<Envelope, TOutgoing>>(buildOutgoing);

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

    /// <summary>
    /// Build the per-message incoming dispatch by composing an open-instance setter
    /// delegate per registered <c>_envelopeToHeader</c> entry (skipping properties
    /// that have a custom <see cref="MapIncomingProperty"/> override) and then
    /// appending the user-supplied callbacks. Replaces the FastExpressionCompiler
    /// path. See class XML doc.
    /// </summary>
    private Action<Envelope, TIncoming> buildIncoming()
    {
        var actions = new List<Action<Envelope, TIncoming>>(_envelopeToHeader.Count + _incomingToEnvelope.Count + 1);
        // Adapter: writeIncomingHeaders takes (TIncoming, Envelope) for backward
        // compatibility with subclass overrides; flip the argument order here
        // so the dispatch list can be uniformly Action<Envelope, TIncoming>.
        actions.Add((env, inc) => writeIncomingHeaders(inc, env));

        // Use the default header read for a property unless the caller has
        // supplied a custom incoming mapping for it. Previously this predicate
        // was accidentally checking _envelopeToOutgoing, which caused
        // MapOutgoingProperty to silently delete the incoming header read for
        // the same property. See https://github.com/JasperFx/wolverine/issues/2551.
        foreach (var pair in _envelopeToHeader.Where(x => !_incomingToEnvelope.ContainsKey(x.Key)))
        {
            actions.Add(buildIncomingReader(pair.Key, pair.Value));
        }

        foreach (var pair in _incomingToEnvelope)
        {
            actions.Add(pair.Value);
        }

        var array = actions.ToArray();
        return (envelope, incoming) =>
        {
            for (var i = 0; i < array.Length; i++) array[i](envelope, incoming);
        };
    }

    /// <summary>
    /// Build the per-message outgoing dispatch — symmetric to <see cref="buildIncoming"/>.
    /// </summary>
    private Action<Envelope, TOutgoing> buildOutgoing()
    {
        var actions = new List<Action<Envelope, TOutgoing>>(_envelopeToHeader.Count + _envelopeToOutgoing.Count + 1);
        // Adapter: writeOutgoingOtherHeaders takes (TOutgoing, Envelope); flip
        // the argument order so the dispatch list can be uniformly
        // Action<Envelope, TOutgoing>.
        actions.Add((env, outgoing) => writeOutgoingOtherHeaders(outgoing, env));

        // Use the default header write for a property unless the caller has
        // supplied a custom outgoing mapping for it. Previously this predicate
        // was accidentally checking _incomingToEnvelope, which caused
        // MapIncomingProperty to silently delete the outgoing header write for
        // the same property. See https://github.com/JasperFx/wolverine/issues/2551.
        foreach (var pair in _envelopeToHeader.Where(x => !_envelopeToOutgoing.ContainsKey(x.Key)))
        {
            actions.Add(buildOutgoingWriter(pair.Key, pair.Value));
        }

        foreach (var pair in _envelopeToOutgoing)
        {
            actions.Add(pair.Value);
        }

        var array = actions.ToArray();
        return (envelope, outgoing) =>
        {
            for (var i = 0; i < array.Length; i++) array[i](envelope, outgoing);
        };
    }

    /// <summary>
    /// Build a single per-property incoming reader: pull the typed value from the
    /// transport message via the right <c>read*</c> helper, then apply it to the
    /// envelope via an open-instance setter delegate. The setter delegate is
    /// constructed once via <see cref="MethodInfo.CreateDelegate(Type)"/> — no
    /// per-message reflection.
    /// </summary>
    private Action<Envelope, TIncoming> buildIncomingReader(PropertyInfo prop, string headerKey)
    {
        var setter = prop.SetMethod
            ?? throw new InvalidOperationException(
                $"Envelope property {prop.Name} has no settable accessor; cannot build EnvelopeMapper incoming reader.");

        var propType = prop.PropertyType;

        if (propType == typeof(string))
        {
            var typed = (Action<Envelope, string?>)setter.CreateDelegate(typeof(Action<Envelope, string?>));
            return (env, inc) => typed(env, readString(inc, headerKey));
        }
        if (propType == typeof(Uri))
        {
            var typed = (Action<Envelope, Uri?>)setter.CreateDelegate(typeof(Action<Envelope, Uri?>));
            return (env, inc) => typed(env, readUri(inc, headerKey));
        }
        if (propType == typeof(Guid))
        {
            var typed = (Action<Envelope, Guid>)setter.CreateDelegate(typeof(Action<Envelope, Guid>));
            return (env, inc) => typed(env, readGuid(inc, headerKey));
        }
        if (propType == typeof(bool))
        {
            var typed = (Action<Envelope, bool>)setter.CreateDelegate(typeof(Action<Envelope, bool>));
            return (env, inc) => typed(env, readBoolean(inc, headerKey));
        }
        if (propType == typeof(DateTimeOffset))
        {
            var typed = (Action<Envelope, DateTimeOffset>)setter.CreateDelegate(typeof(Action<Envelope, DateTimeOffset>));
            return (env, inc) => typed(env, readDateTimeOffset(inc, headerKey));
        }
        if (propType == typeof(DateTimeOffset?))
        {
            var typed = (Action<Envelope, DateTimeOffset?>)setter.CreateDelegate(typeof(Action<Envelope, DateTimeOffset?>));
            return (env, inc) => typed(env, readNullableDateTimeOffset(inc, headerKey));
        }
        if (propType == typeof(int))
        {
            var typed = (Action<Envelope, int>)setter.CreateDelegate(typeof(Action<Envelope, int>));
            return (env, inc) => typed(env, readInt(inc, headerKey));
        }
        if (propType == typeof(string[]))
        {
            var typed = (Action<Envelope, string[]>)setter.CreateDelegate(typeof(Action<Envelope, string[]>));
            return (env, inc) => typed(env, readStringArray(inc, headerKey));
        }

        // Fallback: treat as string — matches the original expression-tree code
        // which defaulted to readString for unknown property types.
        {
            var typed = (Action<Envelope, string?>)setter.CreateDelegate(typeof(Action<Envelope, string?>));
            return (env, inc) => typed(env, readString(inc, headerKey));
        }
    }

    /// <summary>
    /// Symmetric to <see cref="buildIncomingReader"/>: pull the typed value from the
    /// envelope via an open-instance getter delegate, then push it into the transport
    /// message via the right <c>write*</c> helper.
    /// </summary>
    private Action<Envelope, TOutgoing> buildOutgoingWriter(PropertyInfo prop, string headerKey)
    {
        var getter = prop.GetMethod
            ?? throw new InvalidOperationException(
                $"Envelope property {prop.Name} has no readable accessor; cannot build EnvelopeMapper outgoing writer.");

        var propType = prop.PropertyType;

        if (propType == typeof(string))
        {
            var typed = (Func<Envelope, string?>)getter.CreateDelegate(typeof(Func<Envelope, string?>));
            return (env, outgoing) => writeString(outgoing, headerKey, typed(env));
        }
        if (propType == typeof(Uri))
        {
            var typed = (Func<Envelope, Uri?>)getter.CreateDelegate(typeof(Func<Envelope, Uri?>));
            return (env, outgoing) => writeUri(outgoing, headerKey, typed(env));
        }
        if (propType == typeof(Guid))
        {
            var typed = (Func<Envelope, Guid>)getter.CreateDelegate(typeof(Func<Envelope, Guid>));
            return (env, outgoing) => writeGuid(outgoing, headerKey, typed(env));
        }
        if (propType == typeof(bool))
        {
            var typed = (Func<Envelope, bool>)getter.CreateDelegate(typeof(Func<Envelope, bool>));
            return (env, outgoing) => writeBoolean(outgoing, headerKey, typed(env));
        }
        if (propType == typeof(DateTimeOffset))
        {
            var typed = (Func<Envelope, DateTimeOffset>)getter.CreateDelegate(typeof(Func<Envelope, DateTimeOffset>));
            return (env, outgoing) => writeDateTimeOffset(outgoing, headerKey, typed(env));
        }
        if (propType == typeof(DateTimeOffset?))
        {
            var typed = (Func<Envelope, DateTimeOffset?>)getter.CreateDelegate(typeof(Func<Envelope, DateTimeOffset?>));
            return (env, outgoing) => writeNullableDateTimeOffset(outgoing, headerKey, typed(env));
        }
        if (propType == typeof(int))
        {
            var typed = (Func<Envelope, int>)getter.CreateDelegate(typeof(Func<Envelope, int>));
            return (env, outgoing) => writeInt(outgoing, headerKey, typed(env));
        }
        if (propType == typeof(string[]))
        {
            var typed = (Func<Envelope, string[]?>)getter.CreateDelegate(typeof(Func<Envelope, string[]?>));
            return (env, outgoing) => writeStringArray(outgoing, headerKey, typed(env));
        }

        // Fallback: treat as string.
        {
            var typed = (Func<Envelope, string?>)getter.CreateDelegate(typeof(Func<Envelope, string?>));
            return (env, outgoing) => writeString(outgoing, headerKey, typed(env));
        }
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
