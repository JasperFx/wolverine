using Alba;
using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Tracking;

namespace Wolverine.Bobcat.Http;

/// <summary>
/// <see cref="WolverineScenario" /> with the Wolverine.HTTP acts (GH-4834): POST a command through
/// Alba inside Wolverine's tracked session, so the events the endpoint appends and the messages it
/// cascades are captured exactly as for a message, and every assertion works unchanged after it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The route comes from the request type.</b> <c>WhenPosted(command)</c> finds the one
/// Wolverine.HTTP endpoint that accepts the command's type on POST, filling route parameters from the
/// command's properties. Several endpoints accepting it is refused rather than guessed; pass the route.
/// </para>
/// <para>
/// <b>Any 2xx is success.</b> A 4xx is a <em>refusal</em> — an answer, captured for
/// <see cref="WolverineScenario.ThenRefusedWith(string)" /> and <see cref="ThenResponseIs" /> — and a 5xx is the
/// act failing. Neither is thrown by the act. An <c>Action&lt;Scenario&gt;</c> overload applies Alba
/// overrides — headers, authentication, a status expectation of your own — on top.
/// </para>
/// </remarks>
public class WolverineHttpScenario : WolverineScenario
{
    public WolverineHttpScenario(IAlbaHost host, string? storeName = null) : base(host, storeName)
    {
        AlbaHost = host;
    }

    /// <summary>The Alba host the HTTP acts go through.</summary>
    public IAlbaHost AlbaHost { get; }

    /// <summary>The last HTTP act's response, or null when the last act was not an HTTP call.</summary>
    public IScenarioResult? LastResponse { get; private set; }

    /// <summary>POST <paramref name="command" /> to the Wolverine.HTTP endpoint that accepts its type.</summary>
    /// <remarks>
    /// <paramref name="command" /> may be a partial object — <c>Specify&lt;T&gt;()</c> or a table row —
    /// built before the act; the step shows only the members it specifies.
    /// </remarks>
    public Task WhenPosted(object command, Action<Scenario>? configure = null)
    {
        // Built once, so the route's parameters and the body come from the same object
        var built = Build(command, $"{PartialMatching.ExpectedType(command).Name} is posted");
        return postAsync(command, built, HttpRoutes.For(Host, built), configure);
    }

    /// <summary>POST <paramref name="command" /> to <paramref name="route" />.</summary>
    public Task WhenPosted(object command, string route, Action<Scenario>? configure = null)
        => postAsync(command, Build(command, $"{PartialMatching.ExpectedType(command).Name} is posted to \"{route}\""), route, configure);

    private async Task postAsync(object command, object built, string route, Action<Scenario>? configure)
    {
        LastResponse = null;
        string? responseBody = null;

        // GH-4931: the endpoint's own commits have no envelope; anything it cascades does
        NextActDispatchesNoMessage();

        // {0} is the command, described in full when it fits; the route is escaped out of the format
        await ActAsync($"{{0}} is posted to \"{route.Replace("{", "{{").Replace("}", "}}")}\"", command,
            tracking => tracking.ExecuteAndWaitAsync((Func<IMessageContext, Task>)(async _ =>
            {
                LastResponse = await AlbaHost.Scenario(x =>
                {
                    x.Post.Json(built).ToUrl(route);

                    // The act judges the status itself: any 2xx, a 4xx as a refusal, a 5xx as a failure
                    x.IgnoreStatusCode();
                    configure?.Invoke(x);
                });
            })),
            async outcome =>
            {
                if (LastResponse is null) return outcome;

                var status = LastResponse.Context.Response.StatusCode;
                responseBody = await LastResponse.ReadAsTextAsync();
                _lastResponseBody = responseBody;

                Verdicts.Value("status", status);
                if (SpecReport.IsRecording)
                {
                    SpecReport.For<HttpExchangeReport>().Add("POST", route, status,
                        System.Text.Json.JsonSerializer.Serialize(built, built.GetType()), responseBody);
                }

                return status switch
                {
                    >= 200 and < 300 => outcome,
                    >= 400 and < 500 => outcome with { Refusal = $"{status}: {responseBody}" },
                    _ => outcome with
                    {
                        Error = outcome.Error ?? new SpecificationFailedException($"POST {route} answered {status}: {responseBody}")
                    }
                };
            });
    }

    /// <summary>
    /// POST <paramref name="command" /> to the endpoint that accepts its type and hand back the 2xx
    /// response body, deserialized — or null when the endpoint refused or failed.
    /// </summary>
    public async Task<TResponse?> WhenPosted<TResponse>(object command, Action<Scenario>? configure = null)
    {
        await WhenPosted(command, configure);
        return await readResponseAsync<TResponse>();
    }

    /// <summary>POST <paramref name="command" /> to <paramref name="route" /> and hand back the 2xx response body.</summary>
    public async Task<TResponse?> WhenPosted<TResponse>(object command, string route, Action<Scenario>? configure = null)
    {
        await WhenPosted(command, route, configure);
        return await readResponseAsync<TResponse>();
    }

    private async Task<TResponse?> readResponseAsync<TResponse>()
    {
        if (LastResponse is null || LastAct.Error is not null || LastAct.Refusal is not null) return default;
        return await LastResponse.ReadAsJsonAsync<TResponse>();
    }

    // The last act's response body, read once when the act completed
    private string? _lastResponseBody;

    /// <summary>
    /// The last HTTP act was refused with a ProblemDetails body, and it matches <paramref name="expected" />
    /// when one is given: a partial object naming the members a spec is about, as in
    /// <c>Specify&lt;ProblemDetails&gt;().With(x =&gt; x.Detail, "Email already in use")</c>.
    /// </summary>
    /// <remarks>
    /// The typed form of <c>ThenRefusedWith(reason)</c> for an endpoint, which refuses with a 4xx
    /// response rather than an exception. Matched on <c>Title</c>, <c>Detail</c>, <c>Status</c>,
    /// <c>Type</c> and <c>Instance</c>: whichever the partial names.
    /// </remarks>
    public async Task<ProblemDetails?> ThenRefusedWithProblem(object? expected = null)
    {
        if (expected is not null and not IPartialObject)
            throw new ArgumentException(
                "Describe the expected ProblemDetails as a partial object, Specify<ProblemDetails>().With(...).",
                nameof(expected));

        var partial = expected as IPartialObject;
        using var step = ScenarioRecorder.Step("Then", partial is null || partial.Values.Count == 0
            ? "refused with ProblemDetails"
            : $"refused with {PartialObjects.Describe(partial)}");

        if (LastResponse is null)
        {
            Verdicts.Fail("No HTTP response was captured: the last act was not an HTTP call. A bus-dispatched command refuses by throwing; use ThenRefusedWith<TException>.");
            return null;
        }

        var status = LastResponse.Context.Response.StatusCode;
        Verdicts.Check("status", status is >= 400 and < 500 ? "4xx" : status.ToString(), "4xx");
        if (status is < 400 or >= 500)
        {
            Verdicts.Fact(false, $"Expected the endpoint to refuse with a 4xx ProblemDetails, but it answered {status}: {_lastResponseBody}");
            return null;
        }

        ProblemDetails? problem;
        try
        {
            problem = System.Text.Json.JsonSerializer.Deserialize<ProblemDetails>(_lastResponseBody ?? "",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException)
        {
            problem = null;
        }

        if (problem is null)
        {
            Verdicts.Fact(false, $"Expected a ProblemDetails body with the {status}, but it was: {_lastResponseBody}");
            return null;
        }

        if (partial is not null)
        {
            var run = PropertyCells.Verify(problem, partial);
            if (!run.Succeeded && !Verdicts.Recording)
                throw new SpecificationFailedException(
                    $"ProblemDetails did not match: {string.Join(", ", PropertyCells.Disagreeing(run))}");
        }

        return problem;
    }

    /// <summary>The last HTTP act answered <paramref name="status" />.</summary>
    public void ThenResponseIs(int status)
    {
        using var step = ScenarioRecorder.Step("Then", $"the response is {status}");

        if (LastResponse is null)
        {
            Verdicts.Fail("No HTTP response was captured: the last act was not an HTTP call. A bus-dispatched command refuses by throwing; use ThenValidationFails.");
            return;
        }

        var actual = LastResponse.Context.Response.StatusCode;
        Verdicts.Check("status", actual, status);
        Verdicts.Fact(actual == status, $"Expected status {status} but the response was {actual}");
    }
}
