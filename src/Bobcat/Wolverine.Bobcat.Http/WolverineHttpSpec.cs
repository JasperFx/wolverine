using Alba;
using Bobcat;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Http;

/// <summary>
/// <see cref="WolverineSpec" /> over an Alba host, adding the Wolverine.HTTP acts of
/// <see cref="WolverineHttpScenario" /> (GH-4834).
/// </summary>
public abstract class WolverineHttpSpec : WolverineSpec
{
    protected WolverineHttpSpec(IAlbaHost host, string? storeName = null) : this(new WolverineHttpScenario(host, storeName))
    {
    }

    protected WolverineHttpSpec(WolverineHttpScenario scenario) : base(scenario)
    {
        HttpScenario = scenario;
    }

    /// <summary>The scenario every step delegates to.</summary>
    public WolverineHttpScenario HttpScenario { get; }

    /// <inheritdoc cref="WolverineHttpScenario.AlbaHost" />
    public IAlbaHost AlbaHost => HttpScenario.AlbaHost;

    /// <inheritdoc cref="WolverineHttpScenario.In{TStore}" />
    public new WolverineHttpScenario In<TStore>() where TStore : class => HttpScenario.In<TStore>();

    /// <inheritdoc cref="WolverineHttpScenario.In(string)" />
    public new WolverineHttpScenario In(string storeName) => HttpScenario.In(storeName);

    /// <inheritdoc cref="WolverineHttpScenario.LastResponse" />
    public IScenarioResult? LastResponse => HttpScenario.LastResponse;

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted(object, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http)]
    public Task WhenPosted(object command, Action<Scenario>? configure = null) => HttpScenario.WhenPosted(command, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted(object, string, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route))]
    public Task WhenPosted(object command, string route, Action<Scenario>? configure = null)
        => HttpScenario.WhenPosted(command, route, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted{TResponse}(object, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http)]
    public Task<TResponse?> WhenPosted<TResponse>(object command, Action<Scenario>? configure = null)
        => HttpScenario.WhenPosted<TResponse>(command, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPosted{TResponse}(object, string, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route))]
    public Task<TResponse?> WhenPosted<TResponse>(object command, string route, Action<Scenario>? configure = null)
        => HttpScenario.WhenPosted<TResponse>(command, route, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPostedToRoute" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "POST")]
    public Task WhenPostedToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => HttpScenario.WhenPostedToRoute(command, routeName, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPut(object, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, HttpMethod = "PUT")]
    public Task WhenPut(object command, Action<Scenario>? configure = null) => HttpScenario.WhenPut(command, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPut(object, string, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPut(object command, string route, Action<Scenario>? configure = null)
        => HttpScenario.WhenPut(command, route, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPutToRoute" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "PUT")]
    public Task WhenPutToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => HttpScenario.WhenPutToRoute(command, routeName, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPatched(object, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, HttpMethod = "PATCH")]
    public Task WhenPatched(object command, Action<Scenario>? configure = null) => HttpScenario.WhenPatched(command, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPatched(object, string, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PATCH")]
    public Task WhenPatched(object command, string route, Action<Scenario>? configure = null)
        => HttpScenario.WhenPatched(command, route, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPatchedToRoute" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "PATCH")]
    public Task WhenPatchedToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => HttpScenario.WhenPatchedToRoute(command, routeName, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenDeleted(object, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, HttpMethod = "DELETE")]
    public Task WhenDeleted(object command, Action<Scenario>? configure = null) => HttpScenario.WhenDeleted(command, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenDeleted(object, string, Action{Scenario}?)" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "DELETE")]
    public Task WhenDeleted(object command, string route, Action<Scenario>? configure = null)
        => HttpScenario.WhenDeleted(command, route, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenDeletedToRoute" />
    [EventModelRole(SliceRole.Command, nameof(command))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "DELETE")]
    public Task WhenDeletedToRoute(object command, string routeName, Action<Scenario>? configure = null)
        => HttpScenario.WhenDeletedToRoute(command, routeName, configure);

    /// <inheritdoc cref="WolverineHttpScenario.WhenReading{T}" />
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "GET")]
    public Task<T?> WhenReading<T>(string route, params object[] routeValues) => HttpScenario.WhenReading<T>(route, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenReadingRoute{T}" />
    [EventModelRole(SliceRole.ReadModel, nameof(T))]
    [EventModelTrigger(ActTrigger.Http, RouteName = nameof(routeName), HttpMethod = "GET")]
    public Task<T?> WhenReadingRoute<T>(string routeName, params object[] routeValues)
        => HttpScenario.WhenReadingRoute<T>(routeName, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.TheResponse{T}" />
    public Task<T?> TheResponse<T>() => HttpScenario.TheResponse<T>();

    /// <inheritdoc cref="WolverineHttpScenario.ThenNotFound" />
    public void ThenNotFound() => HttpScenario.ThenNotFound();

    /// <inheritdoc cref="WolverineHttpScenario.ThenResponseIs" />
    public void ThenResponseIs(int status) => HttpScenario.ThenResponseIs(status);

    /// <inheritdoc cref="WolverineHttpScenario.ThenRefusedWithProblem" />
    public Task<Microsoft.AspNetCore.Mvc.ProblemDetails?> ThenRefusedWithProblem(object? expected = null)
        => HttpScenario.ThenRefusedWithProblem(expected);

    // ---- body-less acts (GH-5076) -------------------------------------------------------------

    /// <inheritdoc cref="WolverineHttpScenario.WhenPostedTo(string, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "POST")]
    public Task WhenPostedTo(string route, params object[] routeValues) => HttpScenario.WhenPostedTo(route, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPostedTo(string, Action{Scenario}, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "POST")]
    public Task WhenPostedTo(string route, Action<Scenario> configure, params object[] routeValues)
        => HttpScenario.WhenPostedTo(route, configure, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPutTo(string, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPutTo(string route, params object[] routeValues) => HttpScenario.WhenPutTo(route, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPutTo(string, Action{Scenario}, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPutTo(string route, Action<Scenario> configure, params object[] routeValues)
        => HttpScenario.WhenPutTo(route, configure, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPatchedTo(string, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PATCH")]
    public Task WhenPatchedTo(string route, params object[] routeValues) => HttpScenario.WhenPatchedTo(route, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenPatchedTo(string, Action{Scenario}, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PATCH")]
    public Task WhenPatchedTo(string route, Action<Scenario> configure, params object[] routeValues)
        => HttpScenario.WhenPatchedTo(route, configure, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenDeletedAt(string, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "DELETE")]
    public Task WhenDeletedAt(string route, params object[] routeValues) => HttpScenario.WhenDeletedAt(route, routeValues);

    /// <inheritdoc cref="WolverineHttpScenario.WhenDeletedAt(string, Action{Scenario}, object[])" />
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "DELETE")]
    public Task WhenDeletedAt(string route, Action<Scenario> configure, params object[] routeValues)
        => HttpScenario.WhenDeletedAt(route, configure, routeValues);
}
