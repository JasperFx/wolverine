using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;

namespace Wolverine.Http.Antiforgery;

/// <summary>
///     Generated-code helper that refuses a request the antiforgery (CSRF) middleware marked invalid. GH-5147.
/// </summary>
/// <remarks>
///     Neither ASP.NET Core's <c>UseAntiforgery()</c> middleware nor the CSRF protection middleware that
///     ASP.NET Core 11 injects on its own rejects a request: both only record the outcome on
///     <see cref="IAntiforgeryValidationFeature" />, and leave the refusal to whatever consumes the endpoint.
///     Minimal API's form binding does it; a Wolverine endpoint never did, so a cross-site request to an
///     endpoint that requires validation either reached the handler (a JSON body, nothing reads the form) or
///     surfaced as an unhandled <see cref="InvalidOperationException" /> from <c>ReadFormAsync</c> (a 500).
///     Wolverine now checks the feature before it binds anything and answers 400 with ProblemDetails.
/// </remarks>
public static class AntiforgeryValidation
{
    public static IResult Validate(HttpContext httpContext)
    {
        var feature = httpContext.Features.Get<IAntiforgeryValidationFeature>();
        if (feature == null || feature.IsValid) return WolverineContinue.Result();

        return Results.Problem(
            statusCode: StatusCodes.Status400BadRequest,
            title: "Antiforgery validation failed",
            detail: feature.Error?.Message ?? "The request failed antiforgery (CSRF) validation.");
    }
}
