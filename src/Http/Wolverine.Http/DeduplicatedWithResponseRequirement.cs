using Wolverine.Attributes;
using Wolverine.Persistence;

namespace Wolverine.Http;

/// <summary>
/// GH-4742. How a <c>[DeduplicatedWithResponse]</c> endpoint resolves its key, and who the key belongs to. See
/// <see cref="DeduplicatedWithResponseAttribute" />.
/// </summary>
public sealed class DeduplicatedWithResponseRequirement
{
    /// <summary>Who a key belongs to. Must not be <see cref="DeduplicationScope.None" />.</summary>
    public required DeduplicationScope Scope { get; init; }

    /// <summary>
    /// Where the key comes from. <see cref="ValueSource.Anything" /> means the
    /// <see cref="DeduplicationRequirement.DefaultHeaderName" /> request header, or the header named <see cref="Key" />.
    /// </summary>
    public ValueSource Source { get; init; } = ValueSource.Anything;

    /// <summary>The header, member, route or query key holding the key.</summary>
    public string? Key { get; init; }

    /// <summary>Must a key be present? Default is <see langword="true" />, as for <c>[Deduplicated]</c>.</summary>
    public bool Required { get; init; } = true;

    /// <summary>Claim lifetime, or null for <see cref="DurabilitySettings.DeduplicationWindow" />.</summary>
    public TimeSpan? Window { get; init; }

    internal string KeyName => Key ?? DeduplicationRequirement.DefaultHeaderName;

    public override string ToString()
    {
        var description = Source == ValueSource.Anything
            ? $"DeduplicatedWithResponse ('{KeyName}' header, Scope = {Scope}, Required = {Required}"
            : $"DeduplicatedWithResponse by {Source} '{Key}' (Scope = {Scope}, Required = {Required}";

        if (Window.HasValue) description += $", Window = {Window.Value}";

        return description + ")";
    }
}
