using Wolverine.Attributes;

namespace Wolverine.Http;

/// <summary>
/// GH-5617. The <c>[DeduplicatedWithResponse]</c> settings that <see cref="WolverineHttpOptions.DeduplicateWithResponse" />
/// gives the endpoints it covers. An endpoint with its own attribute keeps whatever that sets, and takes only what it
/// leaves unset from here.
/// </summary>
public class DeduplicatedWithResponseDefaults
{
    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Source" />
    public ValueSource Source { get; set; } = ValueSource.Anything;

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Key" />
    public string? Key { get; set; }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.Required" />
    public bool Required { get; set; } = true;

    /// <summary>Claim lifetime, or null for <see cref="DurabilitySettings.DeduplicationWindow" />.</summary>
    public TimeSpan? Window { get; set; }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.ClaimInTransaction" />
    /// <remarks>
    /// Null, the default, leaves it to <see cref="WolverineHttpOptions.DefaultClaimInTransaction" />. Set either way,
    /// it counts as the endpoint's own choice.
    /// </remarks>
    public bool? ClaimInTransaction { get; set; }

    /// <inheritdoc cref="DeduplicatedWithResponseRequirement.StoreBody" />
    public bool StoreBody { get; set; } = true;

    internal DeduplicatedWithResponseRequirement ToRequirement(DeduplicationScope scope)
    {
        return new DeduplicatedWithResponseRequirement(scope)
        {
            ExplicitSource = Source,
            Key = Key,
            ExplicitRequired = Required,
            Window = Window,
            ExplicitClaimInTransaction = ClaimInTransaction,
            ExplicitStoreBody = StoreBody
        };
    }
}
