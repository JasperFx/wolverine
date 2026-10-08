using Alba;
using Bobcat;
using Bobcat.Engine;
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
/// <see cref="WolverineScenario.ThenRefusedWith" /> and <see cref="ThenResponseIs" /> — and a 5xx is the
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
