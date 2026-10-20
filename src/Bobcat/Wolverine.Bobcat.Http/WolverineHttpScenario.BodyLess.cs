using Alba;
using Bobcat;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Http;

// GH-5076: the HTTP acts that send no body -- POST /proposals/{id}/accept, DELETE /appointments/{id}. Posting `new { }`
// instead works, but the step then reads as an anonymous type.
//
// Each comes twice: with the route's values alone, and with an Alba configure first -- headers, authentication, a
// status expectation of your own -- as WhenPosted and WhenDeleted take it. The configure sits BEFORE the params values
// so the two never compete: a lambda is no object, so a call with one only fits the configure form, and a call
// without one only the other
public partial class WolverineHttpScenario
{
    /// <summary>
    /// GH-5076. POST to <paramref name="route" /> with no body, written as the endpoint's template —
    /// <c>"/api/proposals/{id}/accept"</c> — with its values after it, in order. For an endpoint that takes everything
    /// from its route; a 4xx is a refusal and a 5xx the act failing, as for <see cref="WhenPosted(object, string, Action{Scenario}?)" />.
    /// </summary>
    /// <remarks>
    /// The step reads as the template, which says which endpoint; the filled URL is in the HTTP exchange report. Write
    /// the template as a constant, so the generator can read it: the act reaches the Event Model as an HTTP write with
    /// that route, and no command type, since it sends none.
    /// </remarks>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "POST")]
    public Task WhenPostedTo(string route, params object[] routeValues) => bodyLessAsync("POST", route, null, routeValues);

    /// <summary>
    /// GH-5076. POST to <paramref name="route" /> with no body, as for <see cref="WhenPostedTo(string, object[])" />, with
    /// <paramref name="configure" /> applying Alba overrides — <c>x =&gt; x.WithRequestHeader("x-tenant", "red")</c> — on top.
    /// </summary>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "POST")]
    public Task WhenPostedTo(string route, Action<Scenario> configure, params object[] routeValues)
        => bodyLessAsync("POST", route, configure, routeValues);

    /// <summary>GH-5076. PUT to <paramref name="route" /> with no body, as for <see cref="WhenPostedTo(string, object[])" />.</summary>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPutTo(string route, params object[] routeValues) => bodyLessAsync("PUT", route, null, routeValues);

    /// <summary>GH-5076. PUT to <paramref name="route" /> with no body and Alba overrides, as for <see cref="WhenPostedTo(string, Action{Scenario}, object[])" />.</summary>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPutTo(string route, Action<Scenario> configure, params object[] routeValues)
        => bodyLessAsync("PUT", route, configure, routeValues);

    /// <summary>GH-5076. PATCH <paramref name="route" /> with no body, as for <see cref="WhenPostedTo(string, object[])" />.</summary>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PATCH")]
    public Task WhenPatchedTo(string route, params object[] routeValues) => bodyLessAsync("PATCH", route, null, routeValues);

    /// <summary>GH-5076. PATCH <paramref name="route" /> with no body and Alba overrides, as for <see cref="WhenPostedTo(string, Action{Scenario}, object[])" />.</summary>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "PATCH")]
    public Task WhenPatchedTo(string route, Action<Scenario> configure, params object[] routeValues)
        => bodyLessAsync("PATCH", route, configure, routeValues);

    /// <summary>
    /// GH-5076. DELETE <paramref name="route" /> with no body — <c>WhenDeletedAt("/api/appointments/{id}", id)</c> — as
    /// for <see cref="WhenPostedTo(string, object[])" />. <see cref="WhenDeleted(object, string, Action{Scenario}?)" />
    /// sends a command as the body instead.
    /// </summary>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "DELETE")]
    public Task WhenDeletedAt(string route, params object[] routeValues) => bodyLessAsync("DELETE", route, null, routeValues);

    /// <summary>GH-5076. DELETE <paramref name="route" /> with no body and Alba overrides, as for <see cref="WhenPostedTo(string, Action{Scenario}, object[])" />.</summary>
    [EventModelTrigger(ActTrigger.Http, Route = nameof(route), HttpMethod = "DELETE")]
    public Task WhenDeletedAt(string route, Action<Scenario> configure, params object[] routeValues)
        => bodyLessAsync("DELETE", route, configure, routeValues);

    private Task bodyLessAsync(string verb, string route, Action<Scenario>? configure, object[] routeValues)
    {
        ArgumentNullException.ThrowIfNull(route);
        return httpRoot.sendAsync(verb, null, null, HttpRoutes.Fill(route, routeValues ?? []), $"a {verb} is sent to \"{route}\"",
            configure);
    }
}
