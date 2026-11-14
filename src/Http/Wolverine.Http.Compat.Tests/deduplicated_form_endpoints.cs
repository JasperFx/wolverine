using System.Net;
using System.Net.Http;
using Alba;
using JasperFx.Resources;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Wolverine.Sqlite;

namespace Wolverine.Http.Compat.Tests;

/// <summary>
///     GH-5562: [DeduplicatedWithResponse] on form and file upload endpoints, which require antiforgery validation
///     by default on .NET 11: the form read has to follow that check and the request buffering both.
/// </summary>
public class deduplicated_form_endpoints : IAsyncLifetime
{
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"wolverine-compat-{Guid.NewGuid():N}.db");
    private IAlbaHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await CompatHost.For(configureWolverine: opts =>
        {
            opts.PersistMessagesWithSqlite($"Data Source={_database}");
            opts.Durability.Mode = DurabilityMode.Solo;
        });

        await ((IHost)_host).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_database);
    }

    [Fact]
    public async Task a_repeat_of_a_form_post_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();

        var first = await DeduplicatedForms.PostFormAsync(_host, key, "jeremy");
        var repeat = await DeduplicatedForms.PostFormAsync(_host, key, "jeremy");
        var changed = await DeduplicatedForms.PostFormAsync(_host, key, "other");

        first.StatusCode().ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode().ShouldBe(HttpStatusCode.OK);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        changed.StatusCode().ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task a_repeat_of_an_upload_under_a_new_boundary_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();

        var first = await DeduplicatedForms.UploadAsync(_host, key, "first-boundary", "Quarterly report");
        var repeat = await DeduplicatedForms.UploadAsync(_host, key, "second-boundary", "Quarterly report");
        var changed = await DeduplicatedForms.UploadAsync(_host, key, "third-boundary", "Annual report");

        first.StatusCode().ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode().ShouldBe(HttpStatusCode.OK);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        changed.StatusCode().ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}

/// <summary>
///     GH-5562: with UseAntiforgery(), a token sent as a form field makes ASP.NET Core read the form before the
///     endpoint buffers the body. The token is left out of the fingerprint, because a browser that loads the page
///     again is given a new one for the same request.
/// </summary>
public class deduplicated_form_endpoints_with_a_form_field_antiforgery_token
    : deduplicated_form_endpoints_with_an_antiforgery_token
{
    // The test server's body is a MemoryStream that ASP.NET Core rewinds after its form read, so the body is still
    // there to fingerprint
    public deduplicated_form_endpoints_with_a_form_field_antiforgery_token() : base(forwardOnlyBody: false)
    {
    }
}

/// <summary>
///     GH-5562: as Kestrel delivers the body, forward-only, so the antiforgery form read consumes it before the
///     endpoint buffers it and the fingerprint is taken from the form that was read.
/// </summary>
public class deduplicated_form_endpoints_with_a_form_field_antiforgery_token_and_a_forward_only_body
    : deduplicated_form_endpoints_with_an_antiforgery_token
{
    public deduplicated_form_endpoints_with_a_form_field_antiforgery_token_and_a_forward_only_body()
        : base(forwardOnlyBody: true)
    {
    }
}

public abstract class deduplicated_form_endpoints_with_an_antiforgery_token : IAsyncLifetime
{
    private readonly bool _forwardOnlyBody;
    private readonly string _database = Path.Combine(Path.GetTempPath(), $"wolverine-compat-{Guid.NewGuid():N}.db");
    private IAlbaHost _host = null!;

    protected deduplicated_form_endpoints_with_an_antiforgery_token(bool forwardOnlyBody)
    {
        _forwardOnlyBody = forwardOnlyBody;
    }

    public async ValueTask InitializeAsync()
    {
        _host = await CompatHost.For(
            opts => opts.AutoAntiforgeryOnFormEndpoints(),
            services => services.AddAntiforgery(),
            app =>
            {
                if (_forwardOnlyBody)
                {
                    app.Use((context, next) =>
                    {
                        context.Request.Body = new ForwardOnlyStream(context.Request.Body);
                        return next(context);
                    });
                }

                app.UseAntiforgery();
            },
            opts =>
            {
                opts.PersistMessagesWithSqlite($"Data Source={_database}");
                opts.Durability.Mode = DurabilityMode.Solo;
            });

        await ((IHost)_host).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        SqliteConnection.ClearAllPools();
        File.Delete(_database);
    }

    [Fact]
    public async Task a_repeat_of_a_form_post_with_a_new_token_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();
        var name = $"with-a-token-{_forwardOnlyBody}";

        var first = await DeduplicatedForms.PostFormAsync(_host, key, name, newTokens());
        var repeat = await DeduplicatedForms.PostFormAsync(_host, key, name, newTokens());
        var changed = await DeduplicatedForms.PostFormAsync(_host, key, "other", newTokens());

        first.StatusCode().ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode().ShouldBe(HttpStatusCode.OK);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        changed.StatusCode().ShouldBe(HttpStatusCode.UnprocessableEntity);

        DeduplicatedFormEndpoints.Runs(name).ShouldBe(1);
    }

    [Fact]
    public async Task a_repeat_of_an_upload_with_a_new_token_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();

        var first = await DeduplicatedForms.UploadAsync(_host, key, "first-boundary", "Quarterly report",
            [1, 2, 3], newTokens());
        var repeat = await DeduplicatedForms.UploadAsync(_host, key, "second-boundary", "Quarterly report",
            [1, 2, 3], newTokens());
        var otherFile = await DeduplicatedForms.UploadAsync(_host, key, "third-boundary", "Quarterly report",
            [1, 2, 4], newTokens());
        var otherTitle = await DeduplicatedForms.UploadAsync(_host, key, "fourth-boundary", "Annual report",
            [1, 2, 3], newTokens());

        first.StatusCode().ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode().ShouldBe(HttpStatusCode.OK);
        (await repeat.ReadAsTextAsync()).ShouldBe(await first.ReadAsTextAsync());
        otherFile.StatusCode().ShouldBe(HttpStatusCode.UnprocessableEntity);
        otherTitle.StatusCode().ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task a_form_post_without_a_token_is_still_refused_with_400()
    {
        await _host.Scenario(x =>
        {
            x.Post.FormData(new Dictionary<string, string> { ["name"] = "jeremy" })
                .ToUrl(DeduplicatedFormEndpoints.FormUrl);
            x.WithRequestHeader("Idempotency-Key", Guid.NewGuid().ToString());
            x.StatusCodeShouldBe(HttpStatusCode.BadRequest);
        });
    }

    // A fresh cookie and request token, as a browser gets when it loads the page that holds the form
    private AntiforgeryTokens newTokens()
    {
        var context = new DefaultHttpContext { RequestServices = _host.Services };
        var tokens = _host.Services.GetRequiredService<IAntiforgery>().GetAndStoreTokens(context);
        var cookieName = _host.Services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name!;

        return new AntiforgeryTokens($"{cookieName}={tokens.CookieToken}", tokens.FormFieldName,
            tokens.RequestToken!);
    }
}

public record AntiforgeryTokens(string Cookie, string FormFieldName, string RequestToken);

/// <summary>A request body that can be read once, front to back, as Kestrel's is</summary>
public class ForwardOnlyStream(Stream inner) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        => inner.ReadAsync(buffer, cancellationToken);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

public static class DeduplicatedForms
{
    public static Task<IScenarioResult> PostFormAsync(IAlbaHost host, string key, string name,
        AntiforgeryTokens? tokens = null)
    {
        var form = new Dictionary<string, string> { ["name"] = name };
        if (tokens != null) form[tokens.FormFieldName] = tokens.RequestToken;

        return host.Scenario(x =>
        {
            x.Post.FormData(form).ToUrl(DeduplicatedFormEndpoints.FormUrl);
            x.WithRequestHeader("Idempotency-Key", key);
            if (tokens != null) x.WithRequestHeader("Cookie", tokens.Cookie);
            x.IgnoreStatusCode();
        });
    }

    public static async Task<IScenarioResult> UploadAsync(IAlbaHost host, string key, string boundary,
        string title, byte[]? file = null, AntiforgeryTokens? tokens = null)
    {
        var content = new MultipartFormDataContent(boundary);
        if (tokens != null) content.Add(new StringContent(tokens.RequestToken), tokens.FormFieldName);
        content.Add(new StringContent(title), "title");
        content.Add(new ByteArrayContent(file ?? [1, 2, 3]), "file", "report.pdf");

        var result = await host.Scenario(x =>
        {
            x.Post.MultipartFormData(content).ToUrl(DeduplicatedFormEndpoints.UploadUrl);
            x.WithRequestHeader("Idempotency-Key", key);
            if (tokens != null) x.WithRequestHeader("Cookie", tokens.Cookie);
            x.IgnoreStatusCode();
        });

        // The boundary has to reach the server unchanged, or a test of a new boundary proves nothing
        result.Context.Request.ContentType.ShouldNotBeNull().ShouldContain(boundary);

        return result;
    }

    public static HttpStatusCode StatusCode(this IScenarioResult result) =>
        (HttpStatusCode)result.Context.Response.StatusCode;
}

public record DeduplicatedReceipt(Guid Id);

public class DeduplicatedFormEndpoints
{
    public const string FormUrl = "/compat/deduplicated/form";
    public const string UploadUrl = "/compat/deduplicated/upload";

    private static readonly Dictionary<string, int> _runs = new();

    public static int Runs(string name)
    {
        lock (_runs) return _runs.GetValueOrDefault(name);
    }

    private static void count(string name)
    {
        lock (_runs) _runs[name] = _runs.GetValueOrDefault(name) + 1;
    }

    // Keyed per test by a fresh Idempotency-Key, but counted by name: give each test its own name
    [DeduplicatedWithResponse(DeduplicationScope.Endpoint)]
    [WolverinePost(FormUrl)]
    public DeduplicatedReceipt PostForm([FromForm] string name)
    {
        count(name);
        return new DeduplicatedReceipt(Guid.NewGuid());
    }

    [DeduplicatedWithResponse(DeduplicationScope.Endpoint)]
    [WolverinePost(UploadUrl)]
    public DeduplicatedReceipt PostUpload(IFormFile file, [FromForm] string title) => new(Guid.NewGuid());
}
