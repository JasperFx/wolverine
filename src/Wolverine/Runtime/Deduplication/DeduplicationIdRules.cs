using System.Diagnostics.CodeAnalysis;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Deduplication;

/// <summary>
/// Derives the logical deduplication id for any message that can be cast to <typeparamref name="T" />
/// from a user supplied lambda. Registered through
/// <c>opts.MessageDeduplication.ByMessage&lt;T&gt;()</c> or
/// <c>opts.Policies.ForMessagesOfType&lt;T&gt;().DeduplicateBy()</c>.
/// </summary>
internal class LambdaDeduplicationIdRule<T> : IDeduplicationIdRule
{
    private readonly Func<T, string?> _source;

    public LambdaDeduplicationIdRule(Func<T, string?> source)
    {
        _source = source;
    }

    public bool Matches(Type messageType) => messageType.CanBeCastTo<T>();

    public void Modify(Envelope envelope)
    {
        // Never overwrite. The publisher's explicit DeliveryOptions.DeduplicationId is applied after
        // the rules run and wins outright, but another rule may have already resolved an id, and a
        // second rule quietly replacing it would make the effective identity depend on registration
        // order.
        if (envelope.DeduplicationId.IsNotEmpty()) return;

        if (envelope.Message is T message)
        {
            var id = _source(message);
            if (id.IsNotEmpty())
            {
                envelope.DeduplicationId = id;
            }
        }
    }

    public override string ToString()
        => $"Deduplication id derived from a lambda on {typeof(T).FullNameInCode()}";
}

/// <summary>
/// Derives the logical deduplication id from a single member of the message type — either one marked
/// with <c>[DeduplicationIdentity]</c>, or one named by
/// <c>opts.MessageDeduplication.ByMemberNamed()</c>.
/// </summary>
internal class MemberDeduplicationIdRule : IDeduplicationIdRule
{
    private readonly Type _messageType;
    private readonly IDeduplicationIdSource _source;
    private readonly string _memberName;

    public MemberDeduplicationIdRule(Type messageType, string memberName, IDeduplicationIdSource source)
    {
        _messageType = messageType;
        _memberName = memberName;
        _source = source;
    }

    public bool Matches(Type messageType) => _messageType.IsAssignableFrom(messageType);

    public void Modify(Envelope envelope)
    {
        if (envelope.DeduplicationId.IsNotEmpty()) return;

        // A route is built for a declared message type, but the envelope can carry a subclass, and
        // the compiled accessor is only valid for the type it was built against.
        if (envelope.Message == null || !_messageType.IsInstanceOfType(envelope.Message)) return;

        var id = _source.Resolve(envelope.Message);
        if (id.IsNotEmpty())
        {
            envelope.DeduplicationId = id;
        }
    }

    public static MemberDeduplicationIdRule For(Type messageType, System.Reflection.MemberInfo member)
    {
        // GH-4811: DeduplicationIdSources.For owns the choice between the compiled accessor and the
        // reflective one, and explains why a native image cannot have the former.
        return new MemberDeduplicationIdRule(messageType, member.Name,
            DeduplicationIdSources.For(messageType, member));
    }

    public override string ToString()
        => $"Deduplication id from {_messageType.FullNameInCode()}.{_memberName}";
}
