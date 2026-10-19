using Alba;
using Bobcat.CritterStack;
using Wolverine;
using Wolverine.Tracking;

namespace OutboxDemo.Tests;

/// <summary>One HTTP call as a step sees it: the status, and the body if there was one.</summary>
public record HttpResult<T>(int StatusCode, T? Body);

/// <summary>
/// The base for every OutboxDemo spec: the HTTP calls a step makes, over Alba, and an empty store before
/// every spec.
/// </summary>
/// <remarks>
/// The status is surfaced rather than asserted. Alba checks for a 200 unless told otherwise, and a spec
/// asserts whatever status it expects, so a 404 a spec is testing for must not fail the call that
/// produced it.
/// </remarks>
public abstract class OutboxDemoSpec(OutboxDemoHost app) : IAsyncLifetime
{
    protected IAlbaHost Alba => app.Host;

    // Not optional: a Marten unique index on (MemberId, EventId) is persistent state, and the duplicate-registration spec leaves a Registration behind. Without the reset the suite passes once per database and then answers 409 for registrations it believes are new.
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

    /// <summary>
    /// Run an HTTP call and wait for every message it cascades to be fully handled. The modules in this
    /// sample talk over <c>UseDurableInbox()</c> local queues, so the response goes out before the next
    /// module has handled the cascade; asserting straight off it would race the handler.
    /// </summary>
    protected async Task<HttpResult<T>> TrackedAsync<T>(Func<Task<HttpResult<T>>> call)
    {
        HttpResult<T>? captured = null;

        // Explicitly typed: ExecuteAndWaitAsync overloads on Task and ValueTask, and an async lambda is
        // convertible to both.
        Func<IMessageContext, Task> act = async _ => { captured = await call(); };

        await Alba.TrackActivity()
            .Timeout(TimeSpan.FromSeconds(30))
            .ExecuteAndWaitAsync(act);

        return captured!;
    }
}
