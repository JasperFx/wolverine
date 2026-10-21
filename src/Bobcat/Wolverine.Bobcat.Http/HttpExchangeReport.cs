using Bobcat.Engine;

namespace Wolverine.Bobcat.Http;

/// <summary>
/// Every HTTP exchange a scenario made — verb, route, status, request and response bodies — as a
/// Bobcat scenario report written out on failure (GH-4834). What you want to see when an HTTP spec
/// goes red, and nothing a green one pays for.
/// </summary>
/// <remarks>
/// GH-5106. The <c>correlation id</c> column is the one the act sent as <c>X-Correlation-ID</c>, which is the
/// correlation id of every message the endpoint went on to cause, so a row here lines up with the rows of the
/// message activity report and with the application's telemetry.
/// </remarks>
public sealed class HttpExchangeReport : TableReport
{
    private const int MaxBody = 2000;

    public override string Title => "HTTP exchanges";

    public void Add(string verb, string route, int status, string? request, string? response)
        => Add(verb, route, status, request, response, null);

    /// <summary>
    /// GH-5106. Add one exchange, with the correlation id the request carried.
    /// </summary>
    public void Add(string verb, string route, int status, string? request, string? response, string? correlationId)
    {
        var failed = status >= 500;
        Row(new List<CellResult>
        {
            new("verb", ResultStatus.ok, verb),
            new("route", ResultStatus.ok, route),
            failed
                ? new CellResult("status", ResultStatus.failed) { Expected = "", Actual = status.ToString() }
                : new CellResult("status", ResultStatus.ok, status.ToString()),
            new("request", ResultStatus.ok, truncate(request)),
            new("response", ResultStatus.ok, truncate(response)),
            new("correlation id", ResultStatus.ok, correlationId ?? "")
        });
    }

    private static string truncate(string? body)
        => body is null ? "" : body.Length <= MaxBody ? body : body[..MaxBody] + $"… ({body.Length - MaxBody} more characters)";
}
