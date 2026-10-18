using Alba;
using Bobcat;
using Bobcat.Engine;
using Bobcat.Runtime;
using Microsoft.AspNetCore.Mvc;
using JasperFx.Events.EventModeling;
using Wolverine.Http.Runtime;
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

    /// <summary>
    /// GH-4977. A view of <paramref name="root" />'s specification pinned to <paramref name="store" />, from
    /// <see cref="In{TStore}" />: its HTTP acts are <paramref name="root" />'s, and so is the last response.
    /// </summary>
    protected WolverineHttpScenario(WolverineHttpScenario root, JasperFx.Events.IEventStore store) : base(root, store)
    {
        _httpOrigin = root.httpRoot;
        AlbaHost = root.AlbaHost;
    }

    private readonly WolverineHttpScenario? _httpOrigin;
    private WolverineHttpScenario httpRoot => _httpOrigin ?? this;

    /// <summary>GH-4977. An HTTP view, so <c>WhenPosted</c> and the response steps work on <c>In&lt;T&gt;()</c> too.</summary>
    protected override global::Bobcat.CritterStack.CritterStackScenario createView(JasperFx.Events.IEventStore store)
        => new WolverineHttpScenario(this, store);

    /// <inheritdoc cref="WolverineScenario.In{TStore}" />
    public new WolverineHttpScenario In<TStore>() where TStore : class => (WolverineHttpScenario)base.In<TStore>();

    /// <inheritdoc cref="WolverineScenario.In(string)" />
    public new WolverineHttpScenario In(string storeName) => (WolverineHttpScenario)base.In(storeName);

    /// <summary>The Alba host the HTTP acts go through.</summary>
    public IAlbaHost AlbaHost { get; }

    /// <summary>The last HTTP act's response, or null when the last act was not an HTTP call.</summary>
    public IScenarioResult? LastResponse
    {
        get => httpRoot._lastResponse;
        private set => httpRoot._lastResponse = value;
    }

    private IScenarioResult? _lastResponse;

    /// <summary>POST <paramref name="command" /> to the Wolverine.HTTP endpoint that accepts its type.</summary>
    /// <remarks>
    /// <paramref name="command" /> may be a partial object — <c>Specify&lt;T&gt;()</c> or a table row —
    /// built before the act; the step shows only the members it specifies.
    /// </remarks>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http)]
    public Task WhenPosted(object command, Action<Scenario>? configure = null)
        => commandAsync("POST", command, null, null, configure);

    /// <summary>POST <paramref name="command" /> to <paramref name="route" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route))]
    public Task WhenPosted(object command, string route, Action<Scenario>? configure = null)
        => commandAsync("POST", command, route, null, configure);

    /// <summary>
    /// POST <paramref name="command" /> to the endpoint whose ASP.NET Core route name is <paramref name="routeName" />
    /// — <c>[WolverinePost("/api/orders", RouteName = "PlaceOrder")]</c> — with route parameters filled from the
    /// command's properties of the same name. A constant route name reaches the Event Model as that endpoint's route.
    /// </summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "POST")]
    public Task WhenPostedToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => commandAsync("POST", command, null, routeName, configure);

    /// <summary>PUT <paramref name="command" /> to the Wolverine.HTTP endpoint that accepts its type on PUT.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, HttpMethod = "PUT")]
    public Task WhenPut(object command, Action<Scenario>? configure = null)
        => commandAsync("PUT", command, null, null, configure);

    /// <summary>PUT <paramref name="command" /> to <paramref name="route" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPut(object command, string route, Action<Scenario>? configure = null)
        => commandAsync("PUT", command, route, null, configure);

    /// <summary>PUT <paramref name="command" /> to the endpoint named <paramref name="routeName" />, as for <see cref="WhenPostedToRoute" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "PUT")]
    public Task WhenPutToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => commandAsync("PUT", command, null, routeName, configure);

    /// <summary>PATCH <paramref name="command" /> to the Wolverine.HTTP endpoint that accepts its type on PATCH.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, HttpMethod = "PATCH")]
    public Task WhenPatched(object command, Action<Scenario>? configure = null)
        => commandAsync("PATCH", command, null, null, configure);

    /// <summary>PATCH <paramref name="command" /> to <paramref name="route" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PATCH")]
    public Task WhenPatched(object command, string route, Action<Scenario>? configure = null)
        => commandAsync("PATCH", command, route, null, configure);

    /// <summary>PATCH <paramref name="command" /> to the endpoint named <paramref name="routeName" />, as for <see cref="WhenPostedToRoute" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "PATCH")]
    public Task WhenPatchedToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => commandAsync("PATCH", command, null, routeName, configure);

    /// <summary>
    /// Send <paramref name="command" /> as the JSON body of a DELETE to the Wolverine.HTTP endpoint that accepts its
    /// type on DELETE, route parameters filled from its properties.
    /// </summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, HttpMethod = "DELETE")]
    public Task WhenDeleted(object command, Action<Scenario>? configure = null)
        => commandAsync("DELETE", command, null, null, configure);

    /// <summary>Send <paramref name="command" /> as the JSON body of a DELETE to <paramref name="route" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "DELETE")]
    public Task WhenDeleted(object command, string route, Action<Scenario>? configure = null)
        => commandAsync("DELETE", command, route, null, configure);

    /// <summary>DELETE to the endpoint named <paramref name="routeName" /> with <paramref name="command" /> as the body, as for <see cref="WhenPostedToRoute" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "DELETE")]
    public Task WhenDeletedToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => commandAsync("DELETE", command, null, routeName, configure);

    /// <summary>
    /// GET <paramref name="route" />, written as the endpoint's template — <c>"/api/appointments/{id}"</c> — with its
    /// values after it, in order, and hand back the 2xx body read as <typeparamref name="T" />: or null when the
    /// endpoint answered a 4xx (a 404 included, for <see cref="ThenNotFound" />) or failed.
    /// </summary>
    /// <remarks>
    /// A GET trigger beside a read role and no command makes this a <b>read act</b> (bobcat#501): a spec whose last act
    /// it is describes a <b>Query</b> slice that reads <typeparamref name="T" />, with the template as its route. Streams
    /// the spec arranged with <c>GivenEvents</c> are setup and stamp nothing. Write the template as a constant, so the
    /// generator can read it.
    /// </remarks>
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "GET")]
    public Task<T?> WhenReading<T>(string route, params object[] routeValues)
        => httpRoot.readAsync<T>(route, HttpRoutes.Fill(route, routeValues));

    /// <summary>
    /// GET the endpoint whose ASP.NET Core route name is <paramref name="routeName" /> —
    /// <c>[WolverineGet("/api/appointments/{id}", RouteName = "GetAppointment")]</c> — its template filled with
    /// <paramref name="routeValues" /> in order, as for <see cref="WhenReading{T}" />. The generator resolves a constant
    /// route name to the endpoint's template, so the Query slice carries both the route and the route name.
    /// </summary>
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "GET")]
    public Task<T?> WhenReadingRoute<T>(string routeName, params object[] routeValues)
    {
        var template = HttpRoutes.Named(Host, routeName, "GET");
        return httpRoot.readAsync<T>(template, HttpRoutes.Fill(template, routeValues));
    }

    private async Task<T?> readAsync<T>(string template, string url)
    {
        // The step reads as the template, which says which endpoint; the filled URL is in the HTTP exchange report
        await sendAsync("GET", null, null, url, $"{typeof(T).Name} is read from \"{template}\"", null);
        return await readResponseAsync<T>();
    }

    // POST / PUT / PATCH / DELETE a command: built once, so the route's parameters and the body come from the same object
    private Task commandAsync(string verb, object command, string? route, string? routeName, Action<Scenario>? configure)
    {
        var name = PartialMatching.ExpectedType(command).Name;
        var phrase = verb switch
        {
            "POST" => "is posted to",
            "PUT" => "is put to",
            "PATCH" => "is patched to",
            _ => "is sent as a DELETE to"
        };

        var built = Build(command, route is null ? $"{name} {phrase}" : $"{name} {phrase} \"{route}\"");
        var url = route
                  ?? (routeName is null
                      ? HttpRoutes.For(Host, built, verb)
                      : HttpRoutes.FillFrom(HttpRoutes.Named(Host, routeName, verb), built));

        // {0} is the command, described in full when it fits; the route is escaped out of the format
        return httpRoot.sendAsync(verb, command, built, url,
            $"{{0}} {phrase} \"{url.Replace("{", "{{").Replace("}", "}}")}\"", configure);
    }

    private async Task sendAsync(string verb, object? command, object? built, string route, string step,
        Action<Scenario>? configure)
    {
        LastResponse = null;
        string? responseBody = null;

        // GH-4931: the endpoint's own commits have no envelope; anything it cascades does
        NextActDispatchesNoMessage();

        Func<TrackedSessionConfiguration, Task<ITrackedSession>> dispatch = tracking => tracking.ExecuteAndWaitAsync(
            (Func<IMessageContext, Task>)(async _ =>
            {
                LastResponse = await AlbaHost.Scenario(x =>
                {
                    switch (verb)
                    {
                        case "GET": x.Get.Url(route); break;
                        case "PUT": x.Put.Json(built!).ToUrl(route); break;
                        case "PATCH": x.Patch.Json(built!).ToUrl(route); break;
                        case "DELETE": x.Delete.Json(built!).ToUrl(route); break;
                        default: x.Post.Json(built!).ToUrl(route); break;
                    }

                    // The act judges the status itself: any 2xx, a 4xx as a refusal, a 5xx as a failure
                    x.IgnoreStatusCode();
                    configure?.Invoke(x);

                    // GH-4939: the request carries the act's correlation id, which Wolverine.HTTP applies to the
                    // endpoint's messages -- and to its event store session too, with
                    // WolverineHttpOptions.ApplyCorrelationIdHeaderToAllEndpoints -- so ThenEventuallyCaused finds them.
                    // GH-4940: set after configure, so the act's own id wins over one the caller names
                    if (ActCorrelationId is { } correlationId)
                        x.WithRequestHeader(RequestIdMiddleware.CorrelationIdHeaderKey, correlationId);
                });
            }));

        Func<ActOutcome, Task<ActOutcome>> complete = async outcome =>
        {
            if (LastResponse is null) return outcome;

            var status = LastResponse.Context.Response.StatusCode;
            responseBody = await LastResponse.ReadAsTextAsync();
            httpRoot._lastResponseBody = responseBody;

            Verdicts.Value("status", status);
            if (SpecReport.IsRecording)
            {
                SpecReport.For<HttpExchangeReport>().Add(verb, route, status,
                    built is null ? null : System.Text.Json.JsonSerializer.Serialize(built, built.GetType()), responseBody);
            }

            return status switch
            {
                >= 200 and < 300 => outcome,
                >= 400 and < 500 => outcome with { Refusal = $"{status}: {responseBody}" },
                _ => outcome with
                {
                    Error = outcome.Error ?? new SpecificationFailedException($"{verb} {route} answered {status}: {responseBody}")
                }
            };
        };

        if (command is null) await ActAsync(step, dispatch, complete);
        else await ActAsync(step, command, dispatch, complete);
    }

    /// <summary>
    /// POST <paramref name="command" /> to the endpoint that accepts its type and hand back the 2xx
    /// response body, deserialized — or null when the endpoint refused or failed.
    /// </summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http)]
    public async Task<TResponse?> WhenPosted<TResponse>(object command, Action<Scenario>? configure = null)
    {
        await WhenPosted(command, configure);
        return await readResponseAsync<TResponse>();
    }

    /// <summary>POST <paramref name="command" /> to <paramref name="route" /> and hand back the 2xx response body.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route))]
    public async Task<TResponse?> WhenPosted<TResponse>(object command, string route, Action<Scenario>? configure = null)
    {
        await WhenPosted(command, route, configure);
        return await readResponseAsync<TResponse>();
    }

    /// <summary>
    /// The last HTTP act's 2xx response body, read as <typeparamref name="T" /> — or null when it answered a 4xx or
    /// failed. Not a step: the assertion that follows it is. For an act whose overload hands back nothing, such as
    /// <c>WhenPut</c>.
    /// </summary>
    public Task<T?> TheResponse<T>()
    {
        if (LastResponse is null)
            throw new InvalidOperationException("No HTTP response was captured: the last act was not an HTTP call.");
        return readResponseAsync<T>();
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
            Verdicts.Fact(false, $"Expected the endpoint to refuse with a 4xx ProblemDetails, but it answered {status}: {httpRoot._lastResponseBody}");
            return null;
        }

        ProblemDetails? problem;
        try
        {
            problem = System.Text.Json.JsonSerializer.Deserialize<ProblemDetails>(httpRoot._lastResponseBody ?? "",
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        }
        catch (System.Text.Json.JsonException)
        {
            problem = null;
        }

        if (problem is null)
        {
            Verdicts.Fact(false, $"Expected a ProblemDetails body with the {status}, but it was: {httpRoot._lastResponseBody}");
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
    public void ThenResponseIs(int status) => checkStatus($"the response is {status}", status);

    /// <summary>
    /// The last HTTP act answered 404: a read of something that does not exist, as Wolverine.HTTP answers for a missing
    /// <c>[Document]</c>, <c>[Entity]</c> or <c>[ReadAggregate]</c>.
    /// </summary>
    public void ThenNotFound() => checkStatus("nothing is found (404)", 404);

    private void checkStatus(string stepText, int status)
    {
        using var step = ScenarioRecorder.Step("Then", stepText);

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
