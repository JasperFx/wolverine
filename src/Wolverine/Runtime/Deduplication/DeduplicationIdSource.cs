using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using JasperFx.Core.Reflection;

namespace Wolverine.Runtime.Deduplication;

/// <summary>
/// Reads the logical deduplication id off one member of a message. Split out from
/// <see cref="MemberDeduplicationIdRule" /> so the compiled accessor can be strongly typed over the
/// member's own type without the rule itself becoming generic.
/// </summary>
internal interface IDeduplicationIdSource
{
    string? Resolve(object message);
}

internal static class DeduplicationIdSources
{
    /// <summary>
    /// GH-4811. Builds the accessor for a deduplication identity member.
    ///
    /// <para>
    /// Closing <see cref="MemberDeduplicationIdSource{TMessage,TValue}" /> reflectively is the fast path and
    /// stays the only path under the JIT. It cannot be the path under Native AOT: the second type argument is
    /// the member's own type, and a <c>Guid</c> or a <c>long</c> identity is the common case —
    /// <c>[DeduplicationIdentity]</c> needs no configuration at all, so this closes while a
    /// <see cref="Wolverine.Runtime.Routing.MessageRoute" /> is built, which an app with an external sending
    /// endpoint does at startup. GH-4805 measured that <c>[DynamicDependency]</c> preserves metadata and not
    /// code, so a root over a value-type instantiation is silently ineffective, and a rule object is not a
    /// frame, so <see cref="Wolverine.Configuration.IAotRootSource" /> cannot collect it either.
    /// </para>
    ///
    /// <para>
    /// So, as in GH-4805: rather than root the instantiation, do not create one. ILC substitutes
    /// <see cref="RuntimeFeature.IsDynamicCodeSupported" /> with <c>false</c> and removes this branch
    /// outright. Nothing is given up by the fallback: JasperFx's <c>LambdaBuilder.Getter</c> — the only thing
    /// the generic buys — degrades to a reflective accessor on exactly the same condition.
    /// </para>
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "GH-4811. Closes MemberDeduplicationIdSource<,> over (messageType, member type), which only ever runs under the JIT -- the branch is removed from a native image by the IsDynamicCodeSupported substitution, which uses ReflectiveDeduplicationIdSource instead. The member itself comes from a [DeduplicationIdentity] on the application's own message type, statically rooted by message routing.")]
    public static IDeduplicationIdSource For(Type messageType, MemberInfo member)
    {
        if (RuntimeFeature.IsDynamicCodeSupported)
        {
            return typeof(MemberDeduplicationIdSource<,>)
                .CloseAndBuildAs<IDeduplicationIdSource>(member, messageType, member.GetMemberType()!);
        }

        return new ReflectiveDeduplicationIdSource(member);
    }
}

/// <summary>
/// GH-4811. The Native AOT twin of <see cref="MemberDeduplicationIdSource{TMessage,TValue}" />, reading the
/// identity member reflectively so that nothing has to close a generic over the member's type. See
/// <see cref="DeduplicationIdSources.For" /> for why that matters.
/// </summary>
internal class ReflectiveDeduplicationIdSource : IDeduplicationIdSource
{
    private readonly Func<object, object?> _source;

    public ReflectiveDeduplicationIdSource(MemberInfo member)
    {
        _source = member switch
        {
            PropertyInfo property => message => property.GetValue(message),
            FieldInfo field => message => field.GetValue(message),

            // Unreachable from either caller -- both only ever find a property or a field -- but a silent
            // null accessor here would turn into "deduplication quietly stopped working" at runtime.
            _ => throw new ArgumentOutOfRangeException(nameof(member),
                $"{member.Name} on {member.DeclaringType?.FullNameInCode()} is a {member.MemberType}; a deduplication identity has to be a property or a field")
        };
    }

    public string? Resolve(object message)
    {
        return _source(message)?.ToString();
    }
}

internal class MemberDeduplicationIdSource<TMessage, TValue> : IDeduplicationIdSource
{
    private readonly Func<TMessage, TValue> _source;

    [UnconditionalSuppressMessage("Trimming", "IL2026",
        Justification = "LambdaBuilder.Getter compiles a member-access expression via FastExpressionCompiler. The member originates from a [DeduplicationIdentity] on the application's own message type, which is statically rooted by message routing. See AOT guide.")]
    [UnconditionalSuppressMessage("AOT", "IL3050",
        Justification = "Member-access lambda compiled via FastExpressionCompiler; AOT consumers running pre-generated handlers via TypeLoadMode.Static avoid this code path.")]
    public MemberDeduplicationIdSource(MemberInfo member)
    {
        _source = LambdaBuilder.Getter<TMessage, TValue>(member);
    }

    public string? Resolve(object message)
    {
        return _source((TMessage)message)?.ToString();
    }
}
