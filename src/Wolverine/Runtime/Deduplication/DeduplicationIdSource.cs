using System.Diagnostics.CodeAnalysis;
using System.Reflection;
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
