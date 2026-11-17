using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using Shouldly;
using Xunit;

namespace Wolverine.Http.Tests;

public class fingerprinting_a_deduplicated_request
{
    private static async Task<HttpContext> multipart(string boundary, params string[] parts)
    {
        using var content = new MultipartFormDataContent(boundary);
        for (var i = 0; i < parts.Length; i++)
        {
            content.Add(new StringContent(parts[i]), "part" + i);
        }

        return request(content.Headers.ContentType!.ToString(), await content.ReadAsByteArrayAsync());
    }

    private static HttpContext request(string contentType, byte[] body)
    {
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/uploads";
        context.Request.ContentType = contentType;
        context.Request.Body = new MemoryStream(body);

        return context;
    }

    private static Task<string?> fingerprint(HttpContext context)
        => HttpHandler.ComputeDeduplicationFingerprintAsync(context, "key");

    [Fact]
    public async Task the_boundary_of_a_multipart_body_is_left_out()
    {
        var first = await fingerprint(await multipart("first-boundary", "one", "two"));
        var second = await fingerprint(await multipart("second-boundary", "one", "two"));

        second.ShouldBe(first);
    }

    [Fact]
    public async Task a_part_header_sent_twice_is_not_the_same_as_its_values_joined()
    {
        static HttpContext part(string headers) => request("multipart/form-data; boundary=boundary",
            Encoding.UTF8.GetBytes(
                $"--boundary\r\nContent-Disposition: form-data; name=\"file\"\r\n{headers}\r\n\r\ncontent\r\n--boundary--\r\n"));

        var twice = await fingerprint(part("X-Values: one\r\nX-Values: two"));
        var joined = await fingerprint(part("X-Values: one,two"));

        joined.ShouldNotBe(twice);
    }

    [Fact]
    public async Task a_boundary_too_long_to_read_is_compared_by_the_bytes_sent()
    {
        var boundary = new string('b', 5000);
        byte[] body = [1, 2, 3];

        var first = await fingerprint(request($"multipart/form-data; boundary={boundary}", body));

        (await fingerprint(request($"multipart/form-data; boundary={boundary}", body))).ShouldBe(first);
        (await fingerprint(request($"multipart/form-data; boundary={boundary}", [1, 2, 4]))).ShouldNotBe(first);
    }

    // A form that middleware read before the body was buffered, as UseAntiforgery() does for a token sent as a
    // form field on Kestrel: the body is gone, and only the form is left
    private static HttpContext formReadAhead(Dictionary<string, StringValues> fields, FormFileCollection? files = null)
    {
        var context = withServices(request("application/x-www-form-urlencoded", []));
        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(fields, files)));

        return context;
    }

    private static HttpContext urlEncoded(string body)
        => withServices(request("application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(body)));

    private static HttpContext withServices(HttpContext context, Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection().AddOptions();
        configure?.Invoke(services);
        context.RequestServices = services.BuildServiceProvider();

        return context;
    }

    private static HttpContext rawMultipart(string boundary, params (string Name, string Value)[] parts)
    {
        var body = new StringBuilder();
        foreach (var (name, value) in parts)
        {
            body.Append($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");
        }

        body.Append($"--{boundary}--\r\n");

        return request($"multipart/form-data; boundary={boundary}", Encoding.UTF8.GetBytes(body.ToString()));
    }

    private static FormFile file(string name, string fileName, byte[] content)
    {
        return new FormFile(new MemoryStream(content), 0, content.Length, name, fileName)
        {
            Headers = new HeaderDictionary
            {
                ["Content-Disposition"] = $"form-data; name=\"{name}\"; filename=\"{fileName}\"",
                ["Content-Type"] = "application/pdf"
            }
        };
    }

    [Fact]
    public async Task a_form_read_before_the_body_was_buffered_is_compared_by_its_fields()
    {
        var first = await fingerprint(formReadAhead(new() { ["name"] = "one", ["title"] = "report" }));

        // A form keeps no order for its fields
        (await fingerprint(formReadAhead(new() { ["title"] = "report", ["name"] = "one" }))).ShouldBe(first);
        (await fingerprint(formReadAhead(new() { ["name"] = "two", ["title"] = "report" }))).ShouldNotBe(first);
        (await fingerprint(formReadAhead(new() { ["name"] = new(["one", "two"]), ["title"] = "report" })))
            .ShouldNotBe(first);
    }

    [Fact]
    public async Task a_form_read_before_the_body_was_buffered_is_compared_by_its_files()
    {
        HttpContext upload(string fileName, byte[] content) => formReadAhead(new() { ["title"] = "report" },
            new FormFileCollection { file("file", fileName, content) });

        var first = await fingerprint(upload("report.pdf", [1, 2, 3]));

        (await fingerprint(upload("report.pdf", [1, 2, 3]))).ShouldBe(first);
        (await fingerprint(upload("report.pdf", [1, 2, 4]))).ShouldNotBe(first);
        (await fingerprint(upload("other.pdf", [1, 2, 3]))).ShouldNotBe(first);
    }

    [Fact]
    public async Task a_form_read_before_the_body_was_buffered_matches_the_same_form_sent_as_a_readable_body()
    {
        // Kestrel's body is gone after an early form read; the test server's is rewound and still readable. Both
        // must fingerprint the same request the same way
        var readAhead = await fingerprint(formReadAhead(new() { ["name"] = "one", ["title"] = "report" }));

        (await fingerprint(urlEncoded("title=report&name=one"))).ShouldBe(readAhead);
    }

    [Fact]
    public async Task the_antiforgery_token_is_left_out_of_a_form()
    {
        // A browser that loads the page again is given a new token for the same request
        var first = await fingerprint(urlEncoded("name=one&__RequestVerificationToken=first"));

        (await fingerprint(urlEncoded("name=one&__RequestVerificationToken=second"))).ShouldBe(first);
        (await fingerprint(formReadAhead(new() { ["name"] = "one", ["__RequestVerificationToken"] = "third" })))
            .ShouldBe(first);
        (await fingerprint(urlEncoded("name=two&__RequestVerificationToken=first"))).ShouldNotBe(first);
    }

    [Fact]
    public async Task a_renamed_antiforgery_field_is_the_one_left_out()
    {
        static HttpContext renamed(string body) => withServices(
            request("application/x-www-form-urlencoded", Encoding.UTF8.GetBytes(body)),
            services => services.Configure<AntiforgeryOptions>(x => x.FormFieldName = "csrf"));

        var first = await fingerprint(renamed("name=one&csrf=first"));

        (await fingerprint(renamed("name=one&csrf=second"))).ShouldBe(first);
        (await fingerprint(renamed("name=one&__RequestVerificationToken=other"))).ShouldNotBe(first);
    }

    [Fact]
    public async Task the_antiforgery_token_part_is_left_out_of_a_multipart_body()
    {
        var first = await fingerprint(withServices(
            rawMultipart("first", ("name", "one"), ("__RequestVerificationToken", "first"))));

        (await fingerprint(withServices(
            rawMultipart("second", ("name", "one"), ("__RequestVerificationToken", "second"))))).ShouldBe(first);
        (await fingerprint(withServices(
            rawMultipart("third", ("name", "two"), ("__RequestVerificationToken", "first"))))).ShouldNotBe(first);
    }

    [Fact]
    public async Task a_body_hashed_as_its_bytes_keeps_the_fingerprint_it_had_before()
    {
        // Claims stored by an earlier version are compared with this, so it must never change
        var body = Encoding.UTF8.GetBytes("""{"name":"one"}""");

        var expected = Convert.ToHexStringLower(SHA256.HashData(
            [.. Encoding.UTF8.GetBytes("POST /uploads"), 0, 0, .. body]));

        (await fingerprint(request("application/json", body))).ShouldBe(expected);
    }

    [Fact]
    public async Task a_body_hashed_as_its_parts_never_matches_the_same_bytes_sent_as_another_type()
    {
        // What the multipart body is hashed as, sent raw: before the markers this was the same fingerprint
        var context = await multipart("boundary", "one", "two");
        var parts = await (Task<byte[]?>)typeof(HttpHandler)
            .GetMethod("hashMultipartSectionsAsync", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [context, new byte[16 * 1024]])!;

        context.Request.Body.Position = 0;
        var multipartFingerprint = await fingerprint(context);

        (await fingerprint(request("application/octet-stream", parts!))).ShouldNotBe(multipartFingerprint);
    }

    [Fact]
    public async Task a_boundary_longer_than_the_readers_default_buffer_is_read_when_the_limit_allows_it()
    {
        static HttpContext longBoundary(char boundary, string value) => withServices(
            rawMultipart(new string(boundary, 4500), ("name", value)),
            services => services.Configure<FormOptions>(x => x.MultipartBoundaryLengthLimit = 5000));

        var first = await fingerprint(longBoundary('a', "one"));

        (await fingerprint(longBoundary('b', "one"))).ShouldBe(first);
        (await fingerprint(longBoundary('b', "two"))).ShouldNotBe(first);
    }

    [Fact]
    public async Task a_body_that_cannot_be_read_as_multipart_is_compared_by_the_bytes_sent()
    {
        var complete = (MemoryStream)(await multipart("boundary", "one", "two")).Request.Body;
        var truncated = complete.ToArray()[..^20];
        var other = truncated.ToArray();
        other[^1]++;

        var context = request("multipart/form-data; boundary=boundary", truncated);
        var first = await fingerprint(context);

        context.Request.Body.Position.ShouldBe(0);
        (await fingerprint(request("multipart/form-data; boundary=boundary", truncated))).ShouldBe(first);
        (await fingerprint(request("multipart/form-data; boundary=boundary", other))).ShouldNotBe(first);
    }
}
