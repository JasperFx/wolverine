using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using ImTools;
using JasperFx.Core;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Deduplication;

/// <summary>
/// GH-4180 follow up. Application wide rules for deriving <see cref="Envelope.DeduplicationId" /> —
/// the <b>logical</b> identity of a message, as opposed to <see cref="Envelope.Id" />, which
/// identifies one delivery — from the message itself.
///
/// <para>
/// Every rule registered here is applied to outgoing envelopes as an <see cref="IEnvelopeRule" />,
/// and none of them ever overwrite an id that is already set: an explicit
/// <c>DeliveryOptions.DeduplicationId</c> always wins, and the first rule to resolve an id keeps it.
/// </para>
///
/// <para>
/// Deriving an id does not by itself deduplicate anything. Enforcement is <c>[Deduplicated]</c> on
/// the receiving handler / endpoint plus <c>opts.Durability.EnableMessageDeduplication</c>.
/// </para>
/// </summary>
public class MessageDeduplicationRules
{
    private readonly List<IDeduplicationIdRule> _rules = new();

    /// <summary>
    /// Derive the logical deduplication id for any message that can be cast to
    /// <typeparamref name="T" />. Return null or an empty string to leave a particular message
    /// without an id.
    /// </summary>
    /// <example>
    /// <code>
    /// opts.MessageDeduplication.ByMessage&lt;RebuildProjection&gt;(
    ///     x => $"{x.ProjectionName}|{x.Occurrence:O}");
    /// </code>
    /// </example>
    public MessageDeduplicationRules ByMessage<T>(Func<T, string?> source)
    {
        ArgumentNullException.ThrowIfNull(source);

        _rules.Add(new LambdaDeduplicationIdRule<T>(source));
        return this;
    }

    /// <summary>
    /// Derive the logical deduplication id from the first matching property or field name found on
    /// each message type. Useful when your message types are generated (from <c>.proto</c> files,
    /// say) and you can neither mark a member with <c>[DeduplicationIdentity]</c> nor be bothered
    /// writing a lambda per type. Values are converted with <c>ToString()</c>.
    /// </summary>
    /// <param name="memberNames">Member names to look for, in order of preference</param>
    public MessageDeduplicationRules ByMemberNamed(params string[] memberNames)
    {
        if (memberNames == null || memberNames.Length == 0)
        {
            throw new ArgumentException("At least one member name is required", nameof(memberNames));
        }

        _rules.Add(new MemberNameDeduplicationIdRule(memberNames));
        return this;
    }

    internal bool HasAnyRules() => _rules.Count != 0;

    /// <summary>
    /// The rules that can apply to this message type. Resolved once per route rather than per
    /// message, so a rule for an unrelated message type costs nothing on the sending path.
    /// </summary>
    internal IEnumerable<IEnvelopeRule> RulesFor(Type messageType)
    {
        foreach (var rule in _rules)
        {
            if (rule.Matches(messageType))
            {
                yield return rule;
            }
        }
    }
}

/// <summary>
/// Resolves the logical deduplication id by looking for one of a set of member names on each message
/// type. The per-type lookup is cached, so the reflection happens once per message type.
/// </summary>
internal class MemberNameDeduplicationIdRule : IDeduplicationIdRule
{
    private readonly string[] _memberNames;
    private ImHashMap<Type, IDeduplicationIdSource?> _sources = ImHashMap<Type, IDeduplicationIdSource?>.Empty;

    public MemberNameDeduplicationIdRule(string[] memberNames)
    {
        _memberNames = memberNames;
    }

    public bool Matches(Type messageType) => sourceFor(messageType) != null;

    public void Modify(Envelope envelope)
    {
        if (envelope.DeduplicationId.IsNotEmpty()) return;
        if (envelope.Message == null) return;

        var source = sourceFor(envelope.Message.GetType());
        if (source == null) return;

        var id = source.Resolve(envelope.Message);
        if (id.IsNotEmpty())
        {
            envelope.DeduplicationId = id;
        }
    }

    private IDeduplicationIdSource? sourceFor(Type messageType)
    {
        if (_sources.TryFind(messageType, out var source))
        {
            return source;
        }

        source = tryBuildSource(messageType);
        _sources = _sources.AddOrUpdate(messageType, source);

        return source;
    }

    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Member-name deduplication is opt-in; consumers preserve the target member via DAM or a trim descriptor. See AOT guide.")]
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "Closed MemberDeduplicationIdSource<,> resolved from runtime types; AOT consumers preserve via TrimmerRootDescriptor. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Closed MemberDeduplicationIdSource<,> resolved from runtime types; AOT consumers preserve via TrimmerRootDescriptor. See AOT guide.")]
    private IDeduplicationIdSource? tryBuildSource(Type messageType)
    {
        foreach (var memberName in _memberNames)
        {
            MemberInfo? member = messageType.GetProperty(memberName);
            member ??= messageType.GetField(memberName);

            if (member != null)
            {
                return typeof(MemberDeduplicationIdSource<,>)
                    .CloseAndBuildAs<IDeduplicationIdSource>(member, messageType, member.GetMemberType()!);
            }
        }

        return null;
    }

    public override string ToString()
        => $"Deduplication id from the first member named {string.Join(", ", _memberNames)}";
}
