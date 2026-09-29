using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_streaming_multipart_upload
public static class StreamingUploadEndpoint
{
    // Lift Kestrel's 30 MB request body limit and the 128 MB per-section limit
    // for this endpoint only
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    [WolverinePost("/upload/stream")]
    public static async Task<string> Upload(MultipartReader reader, CancellationToken cancellationToken)
    {
        var received = new List<string>();

        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition))
            {
                continue;
            }

            if (disposition.IsFileDisposition())
            {
                // section.Body is the file straight off the wire. Copy it to wherever
                // it is going (blob storage, a file) instead of holding it in memory
                var length = await CountBytesAsync(section.Body, cancellationToken);
                received.Add($"{disposition.Name.Value}:{disposition.FileName.Value}:{length}");
            }
            else if (disposition.IsFormDisposition())
            {
                var value = await section.ReadAsStringAsync(cancellationToken);
                received.Add($"{disposition.Name.Value}={value}");
            }
        }

        return string.Join("|", received);
    }

    private static async Task<long> CountBytesAsync(Stream body, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        long length = 0;
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken)) > 0) length += read;
        return length;
    }
}
#endregion

public static class StreamingUploadProbeEndpoints
{
    [RequestFormLimits(MultipartBodyLengthLimit = 1234, MultipartHeadersCountLimit = 7)]
    [WolverinePost("/upload/stream/limits")]
    public static string Limits(MultipartReader reader)
        => $"{reader.BodyLengthLimit}|{reader.HeadersCountLimit}";

    [WolverinePost("/upload/stream/unbuffered")]
    public static async Task<string> Unbuffered(MultipartReader reader, HttpContext context)
    {
        var sections = 0;
        while (await reader.ReadNextSectionAsync(context.RequestAborted) != null) sections++;

        // Request.Form creates the IFormFeature on first use
        return $"{sections}|{context.Features.Get<IFormFeature>() is null}";
    }
}
