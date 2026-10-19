using Alba;
using Bobcat.CritterStack;

namespace EcommerceMicroservices.Tests;

/// <summary>One HTTP call as a step sees it: the status, and the body if there was one.</summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// The base for every EcommerceMicroservices spec: the HTTP calls a step makes, over Alba, and an empty store before
/// every spec.
/// </summary>
/// <remarks>
/// The status is surfaced rather than asserted. Alba checks for a 200 unless told otherwise, and a spec
/// asserts whatever status it expects, so a 404 a spec is testing for must not fail the call that
/// produced it.
/// </remarks>
public abstract class EcommerceMicroservicesSpec(EcommerceMicroservicesHost app) : IAsyncLifetime
{
    protected IAlbaHost Alba => app.Host;

    // The Catalog host seeds five products at start-up; emptying the store before every spec is what makes "at least 1 product is returned" mean the one the spec created.
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
