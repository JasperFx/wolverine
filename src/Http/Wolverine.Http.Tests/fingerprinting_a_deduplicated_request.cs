using System.Net.Http;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
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

    [Fact]
    public async Task a_form_read_before_the_body_was_buffered_is_refused()
    {
        var context = request("application/x-www-form-urlencoded", []);
        context.Features.Set<IFormFeature>(new FormFeature(
            new FormCollection(new Dictionary<string, StringValues> { ["name"] = "one" })));

        (await Should.ThrowAsync<InvalidOperationException>(() => fingerprint(context)))
            .Message.ShouldContain("was read before its body was buffered");
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
