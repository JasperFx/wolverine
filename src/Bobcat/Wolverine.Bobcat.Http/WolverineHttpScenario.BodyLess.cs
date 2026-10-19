using Bobcat;
using JasperFx.Events.EventModeling;

namespace Wolverine.Bobcat.Http;

// GH-5076: the HTTP acts that send no body -- POST /proposals/{id}/accept, DELETE /appointments/{id}. Posting `new { }`
// instead works, but the step then reads as an anonymous type
public partial class WolverineHttpScenario
{
    /// <summary>
    /// GH-5076. POST to <paramref name="route" /> with no body, written as the endpoint's template —
    /// <c>"/api/proposals/{id}/accept"</c> — with its values after it, in order. For an endpoint that takes everything
    /// from its route; a 4xx is a refusal and a 5xx the act failing, as for <see cref="WhenPosted(object, string, Action{Alba.Scenario}?)" />.
    /// </summary>
    /// <remarks>
    /// The step reads as the template, which says which endpoint; the filled URL is in the HTTP exchange report. Write
    /// the template as a constant, so the generator can read it: the act reaches the Event Model as an HTTP write with
    /// that route, and no command type, since it sends none.
    /// </remarks>
    [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "POST")]
    public Task WhenPostedTo(string route, params object[] routeValues) => bodyLessAsync("POST", route, routeValues);

    /// <summary>GH-5076. PUT to <paramref name="route" /> with no body, as for <see cref="WhenPostedTo" />.</summary>
    [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "PUT")]
    public Task WhenPutTo(string route, params object[] routeValues) => bodyLessAsync("PUT", route, routeValues);

    /// <summary>GH-5076. PATCH <paramref name="route" /> with no body, as for <see cref="WhenPostedTo" />.</summary>
    [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "PATCH")]
    public Task WhenPatchedTo(string route, params object[] routeValues) => bodyLessAsync("PATCH", route, routeValues);

    /// <summary>
    /// GH-5076. DELETE <paramref name="route" /> with no body — <c>WhenDeletedAt("/api/appointments/{id}", id)</c> — as
    /// for <see cref="WhenPostedTo" />. <see cref="WhenDeleted(object, string, Action{Alba.Scenario}?)" /> sends a command
    /// as the body instead.
    /// </summary>
    [EventModelTrigger(TriggerKind.Http, Route = nameof(route), HttpMethod = "DELETE")]
    public Task WhenDeletedAt(string route, params object[] routeValues) => bodyLessAsync("DELETE", route, routeValues);

    private Task bodyLessAsync(string verb, string route, object[] routeValues)
    {
        ArgumentNullException.ThrowIfNull(route);
        return httpRoot.sendAsync(verb, null, null, HttpRoutes.Fill(route, routeValues ?? []), $"a {verb} is sent to \"{route}\"", null);
    }
}
