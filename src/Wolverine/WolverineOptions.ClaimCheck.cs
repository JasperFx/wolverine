using Wolverine.Persistence.ClaimCheck.Internal;

namespace Wolverine;

public sealed partial class WolverineOptions
{
    /// <summary>
    /// Set when <see cref="Wolverine.Persistence.WolverineOptionsClaimCheckExtensions.UseClaimCheck"/>
    /// defers to a DI-registered <see cref="Wolverine.Persistence.IClaimCheckStore"/> (the
    /// <c>...FromServices</c> overloads). The claim-check serializer captures this proxy at
    /// configuration time; the runtime binds it to the built service provider at startup so the
    /// real backend is used instead of the file-system fallback. Null when a store was assigned
    /// explicitly or the file-system fallback is in use. See GH-3564.
    /// </summary>
    internal DeferredClaimCheckStore? DeferredClaimCheckStore { get; set; }
}
