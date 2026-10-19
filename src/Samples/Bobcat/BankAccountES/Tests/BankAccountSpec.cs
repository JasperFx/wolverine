using System.Text.RegularExpressions;
using Alba;
using Bobcat;
using Bobcat.CritterStack;
using JasperFx.Events.EventModeling;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace BankAccountES.Tests;

/// <summary>
/// The base for every BankAccountES spec: Bobcat's store-only <see cref="CritterStackSpec" /> vocabulary
/// (arrange events, assert events, aggregates and read models), plus the two ways this application is
/// driven -- over HTTP and over the Wolverine bus.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the acts live here.</b> <see cref="CritterStackScenario" /> is deliberately store-only, so it has
/// no HTTP or message act of its own. Each act below runs through <see cref="CritterStackSpec.WhenActing" />,
/// which takes the store's high-water mark, runs the act, and reads back everything appended since -- so
/// <c>ThenEvents</c> and <c>ThenNoEvents</c> judge exactly what the request appended.
/// </para>
/// <para>
/// <b>Why they re-declare the attributes.</b> The generator reads <see cref="EventModelRoleAttribute" /> and
/// <see cref="EventModelTriggerAttribute" /> off the method a test calls, never off what that method's body
/// calls. These attributes are what make the last command a test sends its slice's command, and its route
/// the slice's trigger.
/// </para>
/// </remarks>
public abstract class BankAccountSpec(BankAccountHost app) : CritterStackSpec(app.Host)
{
    private int _lastStatusCode;
    private IScenarioResult? _lastResponse;

    protected IAlbaHost Alba => app.Host;

    // ---- acts ------------------------------------------------------------------------------

    /// <summary>
    /// POST <paramref name="command" /> to <paramref name="route" />. Write the route as the endpoint's
    /// template -- <c>"/api/accounts/{accountId}/deposits"</c> -- with its values after it, in order: a constant
    /// template is what reaches the Event Model as the slice's route.
    /// </summary>
    // #region when-posted
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "POST")]
    public Task WhenPosted(object command, string route, params object[] routeValues)
        => send("POST", command, route, routeValues);
    // #endregion when-posted

    /// <summary>PUT <paramref name="command" /> to <paramref name="route" />, written as for <see cref="WhenPosted" />.</summary>
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPut(object command, string route, params object[] routeValues)
        => send("PUT", command, route, routeValues);

    /// <summary>
    /// PUT <paramref name="command" /> to the endpoint whose ASP.NET Core route name is <paramref name="routeName" />,
    /// with the template's values after it, in order (bobcat#500). The generator resolves a constant route name to
    /// the endpoint's template at compile time, so the slice gets the same route a template would have given it.
    /// </summary>
    // #region when-put-to
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "PUT")]
    public Task WhenPutTo(object command, string routeName, params object[] routeValues)
        => send("PUT", command, templateNamed(routeName), routeValues);
    // #endregion when-put-to

    /// <summary>Hand <paramref name="message" /> to its Wolverine handler over the bus, and wait for it to finish.</summary>
    // #region when-received
    [EventModelRole(SliceRole.Command, nameof(message))]
    [EventModelTrigger(ActTrigger.MessageHandler)]
    public Task WhenReceived(object message)
        => WhenActing($"{message.GetType().Name} is received",
            services => services.GetRequiredService<IMessageBus>().InvokeAsync(message));
    // #endregion when-received

    /// <summary>
    /// GET <paramref name="route" /> (a template, as for <see cref="WhenPosted" />) and keep the response, which
    /// is read as a <typeparamref name="T" /> -- the read model a query slice reads.
    /// </summary>
    /// <remarks>
    /// A GET trigger beside a read role and no command makes this a <b>read act</b> (bobcat#501): a spec whose last
    /// act it is describes a Query slice, which reads <typeparamref name="T" /> through <c>ReadsFrom</c> rather than
    /// producing it. Streams the spec arranged with <c>GivenEvents</c> are setup and stamp nothing.
    /// </remarks>
    // #region when-reading
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "GET")]
    public Task WhenReading<T>(string route, params object[] routeValues) where T : class
        => send("GET", null, route, routeValues);
    // #endregion when-reading

    private Task send(string verb, object? command, string route, object[] routeValues)
    {
        var url = fill(route, routeValues);

        // The step reads as the route template, which says which endpoint; the filled URL would only add a GUID
        var description = command is null ? $"{verb} {route} is read" : $"{command.GetType().Name} is sent as {verb} {route}";

        return WhenActing(description, async _ =>
        {
            _lastResponse = await Alba.Scenario(x =>
            {
                switch (verb)
                {
                    case "POST": x.Post.Json(command!).ToUrl(url); break;
                    case "PUT": x.Put.Json(command!).ToUrl(url); break;
                    default: x.Get.Url(url); break;
                }

                // A refusal is an answer to assert on, not an exception
                x.IgnoreStatusCode();
            });

            _lastStatusCode = _lastResponse.Context.Response.StatusCode;
        });
    }

    // The running host's answer to a route name: the template of the endpoint carrying that RouteNameMetadata
    private string templateNamed(string routeName)
    {
        var templates = Alba.Services.GetServices<EndpointDataSource>()
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.Metadata.GetMetadata<IRouteNameMetadata>()?.RouteName == routeName)
            .Select(x => x.RoutePattern.RawText)
            .Distinct()
            .ToList();

        return templates.Count == 1
            ? templates[0]!
            : throw new ArgumentException($"Expected one endpoint with the route name '{routeName}', found {templates.Count}.");
    }

    // "/api/accounts/{accountId}/deposits" + [id] => "/api/accounts/<id>/deposits"
    private static string fill(string route, object[] values)
    {
        var i = 0;
        var url = Regex.Replace(route, @"\{[^}]+\}", m => i < values.Length ? Uri.EscapeDataString(values[i++].ToString()!) : m.Value);
        if (i != values.Length)
            throw new ArgumentException($"Route '{route}' has {i} placeholder(s) but was given {values.Length} value(s).");
        return url;
    }

    // ---- assertions on the response ---------------------------------------------------------

    [Then("the response status is {status}")]
    public void ThenTheResponseStatusIs(int status) => SpecAssert.Check("status", _lastStatusCode, status);

    /// <summary>The last response's body, read as JSON. Not a step: the assertion that follows it is.</summary>
    public Task<T> TheResponse<T>()
        => (_lastResponse ?? throw new InvalidOperationException("No request has been sent yet.")).ReadAsJsonAsync<T>()!;
}
