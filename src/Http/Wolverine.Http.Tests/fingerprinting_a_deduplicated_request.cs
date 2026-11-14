using System.Net.Http;
using System.Security.Cryptography;
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

    // A form that middleware read before the body was buffered, as antiforgery validation of a token sent as a
    // form field does: the body has nothing left in it
    private static HttpContext formReadFirst(Dictionary<string, StringValues> fields, params (string name, string fileName, byte[] content)[] files)
    {
        var context = request("multipart/form-data; boundary=boundary", []);

        var formFiles = new FormFileCollection();
        foreach (var (name, fileName, content) in files)
        {
            formFiles.Add(new FormFile(new MemoryStream(content), 0, content.Length, name, fileName)
            {
                Headers = new HeaderDictionary
                {
                    ["Content-Disposition"] = $"form-data; name=\"{name}\"; filename=\"{fileName}\""
                }
            });
        }

        context.Features.Set<IFormFeature>(new FormFeature(new FormCollection(fields, formFiles)));
        return context;
    }

    [Fact]
    public async Task a_form_read_before_the_body_was_buffered_is_compared_by_its_fields_and_files()
    {
        Dictionary<string, StringValues> fields() => new() { ["title"] = "Quarterly report", ["__token"] = "abc" };

        var first = await fingerprint(formReadFirst(fields(), ("file", "report.pdf", [1, 2, 3])));

        (await fingerprint(formReadFirst(fields(), ("file", "report.pdf", [1, 2, 3])))).ShouldBe(first);

        (await fingerprint(formReadFirst(fields(), ("file", "report.pdf", [1, 2, 4])))).ShouldNotBe(first);
        (await fingerprint(formReadFirst(fields(), ("file", "other.pdf", [1, 2, 3])))).ShouldNotBe(first);
        (await fingerprint(formReadFirst(new() { ["title"] = "Annual report", ["__token"] = "abc" },
            ("file", "report.pdf", [1, 2, 3])))).ShouldNotBe(first);
        (await fingerprint(formReadFirst(fields()))).ShouldNotBe(first);
    }

    [Fact]
    public async Task a_form_field_sent_twice_is_not_the_same_as_its_values_joined()
    {
        var twice = await fingerprint(formReadFirst(new() { ["name"] = new StringValues(["one", "two"]) }));
        var joined = await fingerprint(formReadFirst(new() { ["name"] = "one,two" }));

        joined.ShouldNotBe(twice);
    }

    [Fact]
    public async Task the_fingerprint_of_the_bytes_sent_is_unchanged()
    {
        // Claims stored before an upgrade are compared against this, so it must not move
        byte[] body = """{"name":"one"}"""u8.ToArray();
        var context = request("application/json", body);
        context.Request.QueryString = new QueryString("?draft=true");

        var expected = SHA256.HashData([
            ..Encoding.UTF8.GetBytes("POST /uploads"), 0,
            ..Encoding.UTF8.GetBytes("?draft=true"), 0,
            ..body
        ]);

        (await fingerprint(context)).ShouldBe(Convert.ToHexStringLower(expected));
    }

    [Fact]
    public async Task a_multipart_body_is_never_mistaken_for_the_bytes_sent()
    {
        // Every fingerprint of the bytes sent hashes the method first. A multipart one does not, so no body can
        // be built to collide with it
        var context = await multipart("boundary", "one");
        var body = ((MemoryStream)context.Request.Body).ToArray();

        var asBytes = Convert.ToHexStringLower(SHA256.HashData([
            ..Encoding.UTF8.GetBytes("POST /uploads"), 0, 0, ..body
        ]));

        var multipartFingerprint = await fingerprint(context);
        multipartFingerprint.ShouldNotBe(asBytes);
        (await fingerprint(request("application/octet-stream", body))).ShouldBe(asBytes);
    }

    [Fact]
    public async Task a_boundary_longer_than_the_form_limit_is_compared_by_the_bytes_sent()
    {
        // 129 characters: one over FormOptions.MultipartBoundaryLengthLimit (128 by default), which the endpoint's
        // own read refuses, but short enough for the reader. The parts are the same, so only the bytes sent tell
        // these apart
        static HttpContext part(string boundary) => request($"multipart/form-data; boundary={boundary}",
            Encoding.UTF8.GetBytes(
                $"--{boundary}\r\nContent-Disposition: form-data; name=\"part0\"\r\n\r\none\r\n--{boundary}--\r\n"));

        var first = await fingerprint(part(new string('a', 129)));
        var second = await fingerprint(part(new string('b', 129)));

        // Under the limit, the same parts under a new boundary are the same request
        (await fingerprint(part(new string('b', 128)))).ShouldBe(await fingerprint(part(new string('a', 128))));

        second.ShouldNotBe(first);
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
