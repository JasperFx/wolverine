using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Wolverine.Http.Tests.KestrelBodies;

/// <summary>
/// GH-4941. The Kestrel half of GH-4933 (#4934) and GH-4935 (#4936). Both fixes are pinned at the
/// Wolverine level by tests that stand in for the server: a body stream that throws Kestrel's exception,
/// and chunked framing sent through TestServer. What those cannot prove is ASP.NET Core's side of the
/// contract -- that Kestrel honours [RequestSizeLimit] on a Wolverine endpoint and throws the 413 from
/// the body stream while ReadJsonAsync is reading it, and that it reports CanHaveBody for a chunked
/// request -- because TestServer has no IHttpMaxRequestBodySizeFeature at all. So this one runs real
/// Kestrel on an ephemeral port and talks to it over a real HttpClient and, where HttpClient will not
/// send the framing, a raw socket.
/// </summary>
public class kestrel_backed_request_bodies : IAsyncLifetime
{
    private WebApplication theApp = null!;
    private HttpClient theClient = null!;
    private int thePort;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        // Real Kestrel, ephemeral port. TestServer would never enforce the size limit
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeAssembly(GetType().Assembly);
        });

        builder.Services.AddWolverineHttp();

        theApp = builder.Build();
        theApp.MapWolverineEndpoints(opts =>
        {
            // Only the endpoints in this file. The rest of the test assembly's endpoint types belong to
            // other fixtures and some of them need Marten
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("outside this fixture", t => t.Namespace != typeof(kestrel_backed_request_bodies).Namespace));
        });

        await theApp.StartAsync(TestContext.Current.CancellationToken);

        var url = theApp.Urls.Single();
        thePort = new Uri(url).Port;
        theClient = new HttpClient { BaseAddress = new Uri(url) };
    }

    public async ValueTask DisposeAsync()
    {
        theClient.Dispose();
        await theApp.StopAsync(TestContext.Current.CancellationToken);
        await theApp.DisposeAsync();
    }

    private static string json(string text) => $$"""{"text":"{{text}}"}""";

    // ---- GH-4933: a body Kestrel refuses answers with Kestrel's status ------------------------

    [Fact]
    public async Task a_small_body_under_the_limit_is_fine()
    {
        var response = await theClient.PostAsync("/kestrel/limited",
            new StringContent(json("hi"), Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("ok");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task a_body_over_the_request_size_limit_is_a_413_problem_details(bool chunked)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/kestrel/limited")
        {
            Content = new StringContent(json(new string('x', 4096)), Encoding.UTF8, "application/json")
        };
        request.Headers.TransferEncodingChunked = chunked;
        request.Headers.ConnectionClose = true;

        using var response = await theClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Kestrel threw the 413 from the body stream while ReadJsonAsync was deserializing, and Wolverine
        // answered with Kestrel's status rather than the GH-4528 500
        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");

        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>(TestContext.Current.CancellationToken);
        problem.ShouldNotBeNull();
        problem.Status.ShouldBe(413);
        problem.Title.ShouldBe("Request body could not be read");
        problem.Detail.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task a_malformed_chunk_is_a_400_from_kestrel_not_a_500()
    {
        // HttpClient will not send a bad chunk, so hand-roll one: a chunk size that is not hex
        var raw = $"POST /kestrel/limited HTTP/1.1\r\nHost: 127.0.0.1:{thePort}\r\n" +
                  "Content-Type: application/json\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n" +
                  "zz\r\n{\"text\":\"hi\"}\r\n0\r\n\r\n";

        var (status, body) = await sendRawAsync(raw);

        status.ShouldBe(400);
        body.ShouldContain("Request body could not be read");
    }

    // ---- GH-4935: an optional body sent chunked binds -----------------------------------------

    [Fact]
    public async Task an_optional_body_sent_chunked_is_bound()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/kestrel/optional-body")
        {
            // StreamContent has no computable length, so HttpClient sends it chunked with no Content-Length
            Content = new StreamContent(new MemoryStream(Encoding.UTF8.GetBytes("{\"name\":\"Bob\"}")))
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TransferEncodingChunked = true;

        using var response = await theClient.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("body:Bob");
    }

    [Fact]
    public async Task an_optional_body_with_a_content_length_still_binds()
    {
        using var response = await theClient.PostAsync("/kestrel/optional-body",
            new StringContent("{\"name\":\"Bob\"}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("body:Bob");
    }

    [Fact]
    public async Task an_optional_body_sent_chunked_with_zero_bytes_is_a_400()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/kestrel/optional-body")
        {
            Content = new StreamContent(new MemoryStream())
        };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.TransferEncodingChunked = true;

        using var response = await theClient.SendAsync(request, TestContext.Current.CancellationToken);

        // Kestrel says CanHaveBody for a chunked request, so this is an EMPTY body rather than a missing
        // one: it is read, there is no JSON in it, and the answer is the 400 minimal APIs give here.
        // GH-4935, question 1, decided as minimal API parity
        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task an_optional_body_with_neither_content_length_nor_transfer_encoding_binds_null()
    {
        // HttpClient always frames a POST, so the truly framing-less request needs a raw socket. Kestrel
        // reports CanHaveBody == false here, which is the short-circuit that never reads the body
        var raw = $"POST /kestrel/optional-body HTTP/1.1\r\nHost: 127.0.0.1:{thePort}\r\nConnection: close\r\n\r\n";

        var (status, body) = await sendRawAsync(raw);

        status.ShouldBe(200);
        body.ShouldBe("no-body");
    }

    /// <summary>
    /// One request over a raw socket, returning the status and the decoded body. Connection: close means
    /// the server ends the stream after the response
    /// </summary>
    private async Task<(int Status, string Body)> sendRawAsync(string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, thePort, TestContext.Current.CancellationToken);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), TestContext.Current.CancellationToken);

        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, TestContext.Current.CancellationToken);
        var raw = Encoding.UTF8.GetString(buffer.ToArray());

        var split = raw.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var headerLines = raw[..split].Split("\r\n");
        var body = raw[(split + 4)..];
        var status = int.Parse(headerLines[0].Split(' ')[1]);

        var chunked = headerLines.Skip(1).Any(l =>
            l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) &&
            l.Contains("chunked", StringComparison.OrdinalIgnoreCase));

        if (chunked)
        {
            var decoded = new StringBuilder();
            var rest = body;
            while (true)
            {
                var eol = rest.IndexOf("\r\n", StringComparison.Ordinal);
                var size = Convert.ToInt32(rest[..eol].Split(';')[0], 16);
                if (size == 0) break;
                decoded.Append(rest.Substring(eol + 2, size));
                rest = rest[(eol + 2 + size + 2)..];
            }

            body = decoded.ToString();
        }

        return (status, body);
    }
}

public record LimitedPayload(string Text);

public static class KestrelLimitedEndpoint
{
    // Kestrel enforces this; TestServer has no IHttpMaxRequestBodySizeFeature and ignores it
    [WolverinePost("/kestrel/limited"), RequestSizeLimit(1024)]
    public static string Post(LimitedPayload body) => "ok";
}

public record OptionalPayload(string? Name);

public class KestrelOptionalBodyQuery
{
    [FromQuery] public string? Name { get; set; }

    [FromBody] public OptionalPayload? Body { get; set; }
}

public static class KestrelOptionalBodyEndpoint
{
    [WolverinePost("/kestrel/optional-body")]
    public static string Post([AsParameters] KestrelOptionalBodyQuery query)
        => query.Body is null ? "no-body" : $"body:{query.Body.Name}";
}
