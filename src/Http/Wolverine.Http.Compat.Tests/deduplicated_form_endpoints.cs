using System.Net;
using Alba;
using JasperFx.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Wolverine.Sqlite;

namespace Wolverine.Http.Compat.Tests;

/// <summary>
///     [DeduplicatedWithResponse] on form and file upload endpoints, which require antiforgery validation by
///     default on .NET 11: the form read has to follow that check and the request buffering both.
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

    private async Task<HttpResponseMessage> postAsync(string url, string key, HttpContent content)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        request.Headers.Add("Idempotency-Key", key);

        return await _host.Server.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<string> bodyOf(HttpResponseMessage response)
        => response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task a_repeat_of_a_form_post_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();

        Task<HttpResponseMessage> post(string name) => postAsync("/compat/deduplicated/form", key,
            new FormUrlEncodedContent(new Dictionary<string, string> { ["name"] = name }));

        using var first = await post("jeremy");
        using var repeat = await post("jeremy");
        using var changed = await post("other");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bodyOf(repeat)).ShouldBe(await bodyOf(first));
        changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task a_repeat_of_an_upload_under_a_new_boundary_is_answered_with_the_first_response()
    {
        var key = Guid.NewGuid().ToString();

        Task<HttpResponseMessage> upload(string boundary, string title)
        {
            var content = new MultipartFormDataContent(boundary);
            content.Add(new StringContent(title), "title");
            content.Add(new ByteArrayContent([1, 2, 3]), "file", "report.pdf");

            return postAsync("/compat/deduplicated/upload", key, content);
        }

        using var first = await upload("first-boundary", "Quarterly report");
        using var repeat = await upload("second-boundary", "Quarterly report");
        using var changed = await upload("third-boundary", "Annual report");

        first.StatusCode.ShouldBe(HttpStatusCode.OK);
        repeat.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await bodyOf(repeat)).ShouldBe(await bodyOf(first));
        changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }
}

public record DeduplicatedReceipt(Guid Id);

public class DeduplicatedFormEndpoints
{
    [DeduplicatedWithResponse(DeduplicationScope.Endpoint)]
    [WolverinePost("/compat/deduplicated/form")]
    public DeduplicatedReceipt PostForm([FromForm] string name) => new(Guid.NewGuid());

    [DeduplicatedWithResponse(DeduplicationScope.Endpoint)]
    [WolverinePost("/compat/deduplicated/upload")]
    public DeduplicatedReceipt PostUpload(IFormFile file, [FromForm] string title) => new(Guid.NewGuid());
}
