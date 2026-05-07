using System.Linq.Expressions;
using JasperFx.Core.Reflection;
using Wolverine.ErrorHandling;
using Wolverine.Logging;
using Wolverine.RateLimiting;
using Wolverine.Runtime.Serialization.Encryption;

namespace Wolverine;

public class MessageTypePolicies<T>
{
    private readonly WolverineOptions _parent;

    public MessageTypePolicies(WolverineOptions parent)
    {
        _parent = parent;
    }

    /// <summary>
    ///     Specify that the following members on every message that can be cast
    ///     to type T should be audited as part of telemetry, logging, and metrics
    ///     data exported from this application
    /// </summary>
    /// <param name="members"></param>
    /// <typeparam name="T"></typeparam>
    public MessageTypePolicies<T> Audit(params Expression<Func<T, object>>[] memberExpressions)
    {
        var members = memberExpressions.Select(expr => FindMembers.Determine(expr).First()).ToArray();

        var policy = new AuditMembersPolicy<T>(members);
        _parent.RegisteredPolicies.Insert(0, policy);
        return this;
    }

    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    public MessageTypePolicies<T> AddMiddleware(Type middlewareType)
    {
        var policy = _parent.FindOrCreateMiddlewarePolicy();

        policy.AddType(middlewareType, c => c.InputType().CanBeCastTo<T>()).MatchByMessageType = true;
        return this;
    }

    /// <summary>
    ///     Add middleware only on handlers where the message type can be cast to the message
    ///     type of the middleware type
    /// </summary>
    /// <param name="middlewareType"></param>
    public MessageTypePolicies<T> AddMiddleware<TMiddleware>()
    {
        return AddMiddleware(typeof(TMiddleware));
    }

    /// <summary>
    /// Apply a rate limit schedule to messages assignable to type T
    /// </summary>
    public MessageTypePolicies<T> RateLimit(RateLimit defaultLimit, Action<RateLimitSchedule>? configure = null)
    {
        return RateLimit(null, defaultLimit, configure);
    }

    /// <summary>
    /// Apply a rate limit schedule to messages assignable to type T using a shared key
    /// </summary>
    public MessageTypePolicies<T> RateLimit(string? key, RateLimit defaultLimit,
        Action<RateLimitSchedule>? configure = null)
    {
        var schedule = new RateLimitSchedule(defaultLimit);
        configure?.Invoke(schedule);
        _parent.ConfigureRateLimit(typeof(T), schedule, key);

        return this;
    }

    /// <summary>
    /// Mark messages assignable to <typeparamref name="T"/> as requiring AES-256-GCM
    /// encryption on send and on receive. Resolves the encrypting serializer at the
    /// time this method is invoked, so <see cref="WolverineOptions.UseEncryption"/> or
    /// <see cref="WolverineOptions.RegisterEncryptionSerializer"/> must be called
    /// <b>before</b> this method is invoked. Inbound envelopes of this type whose
    /// content-type is not the encrypted content-type are routed to the dead-letter
    /// queue with <see cref="EncryptionPolicyViolationException"/>.
    /// </summary>
    public MessageTypePolicies<T> Encrypt()
    {
        var encrypting = _parent.TryFindSerializer(EncryptionHeaders.EncryptedContentType)
            ?? throw new InvalidOperationException(
                "No encrypting serializer is registered. Call " +
                "WolverineOptions.UseEncryption(provider) or " +
                "WolverineOptions.RegisterEncryptionSerializer(provider) " +
                $"before .ForMessagesOfType<{typeof(T).Name}>().Encrypt().");

        _parent.MetadataRules.Add(new EncryptMessageTypeRule<T>(encrypting));
        _parent.RequiredEncryptedTypes.Add(typeof(T));

        // Pair the rule for Fault<T> so auto-published fault events for this
        // message type are also routed through the encrypting serializer. The
        // EncryptMessageTypeRule<T>.Modify gate is invariant in T, so a separate
        // rule is required (the one for T does not match Fault<T>).
        //
        // Skipped when T is a value type because Fault<T> is constrained to
        // T : class. The FaultPublisher already silently no-ops on value-type
        // messages, so no Fault<T> is ever produced for those types.
        //
        // Reflective construction keeps Encrypt<T>() callable for any T (no
        // new generic constraint at the entry point). If a user calls Encrypt<T>()
        // for the same T more than once, the rules accumulate in MetadataRules
        // (a list, not a set, mirroring the existing behavior for the T rule);
        // the duplicate rules are behaviorally idempotent — the second swap of
        // Serializer/ContentType writes the same values — but the list grows.
        if (!typeof(T).IsValueType)
        {
            var faultType = typeof(Fault<>).MakeGenericType(typeof(T));
            var faultRuleType = typeof(EncryptMessageTypeRule<>).MakeGenericType(faultType);
            var faultRule = (IEnvelopeRule)Activator.CreateInstance(faultRuleType, encrypting)!;
            _parent.MetadataRules.Add(faultRule);
            _parent.RequiredEncryptedTypes.Add(faultType);
        }

        return this;
    }

    /// <summary>
    /// Opt this message type into auto-published <see cref="Fault{T}"/> events on
    /// terminal handler failure. Overrides any global setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="Fault{T}"/> requires <typeparamref name="T"/> to be a reference type.
    /// Calling this for value-type messages compiles but will not produce a fault at runtime.
    /// </para>
    /// <para>
    /// <b>Delivery semantics.</b> See <see cref="WolverineOptions.PublishFaultEvents(bool)"/>
    /// — auto-published fault events are best-effort and not transactionally co-committed with
    /// the dead-letter-queue move.
    /// </para>
    /// <para>
    /// <b>Scope.</b> See <see cref="WolverineOptions.PublishFaultEvents(bool)"/> — fault events
    /// are not emitted for send-side DLQ movements or unknown-message-type envelopes.
    /// </para>
    /// </remarks>
    public MessageTypePolicies<T> PublishFault(bool includeDiscarded = false)
    {
        _parent.FaultPublishing.SetOverride(typeof(T), includeDiscarded
            ? FaultPublishingMode.DlqAndDiscard
            : FaultPublishingMode.DlqOnly);
        return this;
    }

    /// <summary>
    /// Opt this message type out of auto-published <see cref="Fault{T}"/> events,
    /// even when the global default is on.
    /// </summary>
    public MessageTypePolicies<T> DoNotPublishFault()
    {
        _parent.FaultPublishing.SetOverride(typeof(T), FaultPublishingMode.None);
        return this;
    }
}
