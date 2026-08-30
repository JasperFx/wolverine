using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;

namespace Wolverine.Runtime.Deduplication;

/// <summary>
/// Discovers the <c>[DeduplicationIdentity]</c> convention on a message type and compiles it into an
/// <see cref="IEnvelopeRule" />. The mirror of <c>TopicRouting.DetermineTopicName()</c> for topic
/// names and <c>SagaChain.DetermineSagaIdMember()</c> for saga identity: the message type declares
/// its own identity once, and every publisher gets it without repeating itself.
/// </summary>
internal static class DeduplicationIdentity
{
    private static ImHashMap<Type, IDeduplicationIdRule?> _rules = ImHashMap<Type, IDeduplicationIdRule?>.Empty;

    /// <summary>
    /// The rule for this message type's declared deduplication identity, or null when it declares
    /// none. Cached per message type — the answer is a property of the type's attributes and cannot
    /// change at runtime.
    /// </summary>
    public static IDeduplicationIdRule? TryFindRule(Type messageType)
    {
        if (_rules.TryFind(messageType, out var rule))
        {
            return rule;
        }

        var member = DetermineIdentityMember(messageType);
        rule = member == null ? null : MemberDeduplicationIdRule.For(messageType, member);

        _rules = _rules.AddOrUpdate(messageType, rule);

        return rule;
    }

    /// <summary>
    /// The member carrying this message type's logical deduplication identity, or null for none.
    /// A <c>[DeduplicationIdentity("Name")]</c> on the type itself wins over a marked member, so a
    /// consuming application can point at a different member of a contract it inherits.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Reads the public members of a message type that has opted in with [DeduplicationIdentity]; such types are statically rooted through message routing. See AOT guide.")]
    internal static MemberInfo? DetermineIdentityMember(Type messageType)
    {
        var members = messageType.GetFields().OfType<MemberInfo>()
            .Concat(messageType.GetProperties())
            .ToArray();

        if (messageType.TryGetAttribute<DeduplicationIdentityAttribute>(out var attribute) &&
            attribute.MemberName.IsNotEmpty())
        {
            return members.FirstOrDefault(x => x.Name == attribute.MemberName)
                   ?? throw new InvalidOperationException(
                       $"Message type {messageType.FullNameInCode()} is marked with [DeduplicationIdentity(\"{attribute.MemberName}\")], but has no public property or field by that name");
        }

        var marked = members.Where(x => x.HasAttribute<DeduplicationIdentityAttribute>()).ToArray();

        if (marked.Length > 1)
        {
            throw new InvalidOperationException(
                $"Message type {messageType.FullNameInCode()} has more than one member marked with [DeduplicationIdentity] ({marked.Select(x => x.Name).Join(", ")}). A message has exactly one logical identity; combine the parts with opts.MessageDeduplication.ByMessage<T>() instead");
        }

        return marked.FirstOrDefault();
    }
}
