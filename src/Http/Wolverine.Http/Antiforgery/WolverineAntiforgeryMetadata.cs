using Microsoft.AspNetCore.Antiforgery;

namespace Wolverine.Http.Antiforgery;

internal class WolverineAntiforgeryMetadata : IAntiforgeryMetadata
{
    public static readonly WolverineAntiforgeryMetadata Required = new(true);
    public static readonly WolverineAntiforgeryMetadata NotRequired = new(false);

    private WolverineAntiforgeryMetadata(bool requiresValidation)
    {
        RequiresValidation = requiresValidation;
    }

    public bool RequiresValidation { get; }
}
