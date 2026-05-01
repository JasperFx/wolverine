using System.Linq.Expressions;
using JasperFx.Core.Reflection;
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
        return this;
    }
}