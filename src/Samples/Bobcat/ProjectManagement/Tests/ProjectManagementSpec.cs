using Alba;

namespace ProjectManagement.Tests;

/// <summary>One HTTP call as a step sees it: the status, and the body if there was one.</summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// The base for every ProjectManagement spec: the HTTP calls a step makes, over Alba, and an empty store before
/// every spec.
/// </summary>
/// <remarks>
/// <para>
/// It is Bobcat's store-only <see cref="CritterStackSpec" /> as well, so a spec can assert what an act
/// appended to the event store and what the aggregate became. A When step goes through
/// <see cref="CritterStackSpec.WhenActing" />, which takes the store's high-water mark, runs the request,
/// and reads back everything appended since -- so <c>ThenEvents</c> and <c>ThenNoEvents</c> judge exactly
/// what that request appended, cascades included.
/// </para>
/// <para>
/// The HTTP status is surfaced rather than asserted. Alba checks for a 200 unless told otherwise, and a
/// spec asserts whatever status it expects, so a 404 a spec is testing for must not fail the call that
/// produced it.
/// </para>
/// </remarks>
public abstract class ProjectManagementSpec(ProjectManagementHost app) : CritterStackSpec(app.Host), IAsyncLifetime
{
    protected IAlbaHost Alba => app.Host;

    // Every spec mints its own stream, so the reset is housekeeping: it keeps the event store from growing without bound across runs.
    public async ValueTask InitializeAsync() => await app.Host.ResetEventStoresAsync();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    protected Task<HttpResult<T>> PostJsonAsync<TRequest, T>(string url, TRequest body)
        => SendAsync<T>(s => s.Post.Json(body!).ToUrl(url));

    protected Task<HttpResult<T>> PutJsonAsync<TRequest, T>(string url, TRequest body)
        => SendAsync<T>(s => s.Put.Json(body!).ToUrl(url));

    protected Task<HttpResult<T>> GetJsonAsync<T>(string url)
        => SendAsync<T>(s => s.Get.Url(url));

    protected Task<HttpResult<object>> DeleteAsync(string url)
        => SendAsync<object>(s => s.Delete.Url(url));

    protected async Task<HttpResult<T>> SendAsync<T>(Action<Scenario> configure)
    {
        var result = await Alba.Scenario(s =>
        {
            configure(s);
            s.IgnoreStatusCode();
        });

        T? body = default;
        try { body = await result.ReadAsJsonAsync<T>(); } catch { /* not every response carries JSON */ }

        return new HttpResult<T>(result.Context.Response.StatusCode, body);
    }
}
