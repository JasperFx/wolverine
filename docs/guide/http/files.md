# Uploading Files

As of 1.11.0, Wolverine supports file uploads through the standard ASP.Net Core `IFormFile` or `IFormFileCollection` types. All you need
to do is to have an input parameter to your Wolverine.HTTP endpoint of these types like so:

<!-- snippet: sample_using_file_uploads -->
<a id='snippet-sample_using_file_uploads'></a>
```cs
public class FileUploadEndpoint
{
    // If you have exactly one file upload, take
    // in IFormFile
    [WolverinePost("/upload/file")]
    public static Task Upload(IFormFile file)
    {
        // access the file data
        return Task.CompletedTask;
    }

    // If you have multiple files at one time,
    // use IFormCollection
    [WolverinePost("/upload/files")]
    public static Task Upload(IFormFileCollection files)
    {
        // access files
        return Task.CompletedTask;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FileUploadEndpoint.cs#L9-L31' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_file_uploads' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Upload files in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-7.0)
for more information about these types.

## Multipart Uploads <Badge type="tip" text="5.16" />

Wolverine also supports multipart uploads where you need to combine file uploads with form metadata. You can:

* Use multiple named `IFormFile` parameters, each bound by form field name
* Combine a `[FromForm]` complex type with a separate `IFormFile` parameter
* Use `IFormCollection` for raw access to all form fields and files

<!-- snippet: sample_multipart_upload_endpoints -->
<a id='snippet-sample_multipart_upload_endpoints'></a>
```cs
public static class MultipartUploadEndpoints
{
    // Multiple named file parameters are bound by form field name
    [WolverinePost("/upload/named-files")]
    public static string UploadNamedFiles(IFormFile document, IFormFile thumbnail)
    {
        return $"{document?.FileName}|{document?.Length}|{thumbnail?.FileName}|{thumbnail?.Length}";
    }

    // Combine [FromForm] metadata with a file upload in a single endpoint
    [WolverinePost("/upload/mixed")]
    public static string UploadMixed([FromForm] UploadMetadata metadata, IFormFile file)
    {
        return $"{metadata.Title}|{metadata.Description}|{file?.FileName}|{file?.Length}";
    }

    // Use IFormCollection for raw access to all form data and files
    [WolverinePost("/upload/form-collection")]
    public static string UploadFormCollection(IFormCollection form)
    {
        var keys = string.Join(",", form.Keys.OrderBy(k => k));
        var fileCount = form.Files.Count;
        return $"keys:{keys}|files:{fileCount}";
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FileUploadEndpoint.cs#L76-L103' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_multipart_upload_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Each `IFormFile` parameter is matched to the uploaded file by its parameter name. When sending a multipart request, make sure the form field names match the parameter names in your endpoint method.

## Streaming Large Uploads <Badge type="tip" text="6.42" />

`IFormFile`, `IFormFileCollection`, `IFormCollection` and `[FromForm]` all read the request through `Request.Form`,
which buffers the entire body before your endpoint runs: in memory up to 64 KB per file, on disk beyond that. For
large uploads, take a `MultipartReader` parameter instead. Wolverine opens it over the unbuffered request body and
your endpoint reads each section as it arrives. This is the pattern ASP.NET Core describes in
[Upload large files with streaming](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-10.0#upload-large-files-with-streaming),
without the model binding workarounds MVC needs:

<!-- snippet: sample_streaming_multipart_upload -->
<a id='snippet-sample_streaming_multipart_upload'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/StreamingUploadEndpoints.cs#L10-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_streaming_multipart_upload' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

What Wolverine does for a `MultipartReader` parameter:

* A request that is not `multipart/*` is refused with a `415`. A missing boundary, or one longer than
  `FormOptions.MultipartBoundaryLengthLimit`, gets a `400` with `ProblemDetails`.
* The reader gets the same limits `Request.Form` would use: the registered `FormOptions`, overridden per endpoint by
  `[RequestFormLimits]`. `MultipartBodyLengthLimit` (128 MB by default) applies to each section, and exceeding a limit
  throws `InvalidDataException` while you read.
* The endpoint advertises `multipart/form-data` in its `Accepts` metadata. The parts are whatever your code reads,
  so OpenAPI does not describe them.
* The body can only be read once. Combining a `MultipartReader` with `IFormFile`, `[FromForm]` or a JSON request
  body, or putting it on a `GET` or `HEAD`, fails at startup. Middleware may take the `MultipartReader` too and gets
  the same instance, but any section it reads is gone for the handler.

The server enforces its own limit on the total request body as well: Kestrel's `MaxRequestBodySize` is 30 MB by
default. `[DisableRequestSizeLimit]` or `[RequestSizeLimit(bytes)]` on the endpoint lifts it for that endpoint only,
as in the sample above.

::: warning
With [`AutoAntiforgeryOnFormEndpoints()`](/guide/http/antiforgery), a streaming endpoint requires an antiforgery
token like any other form endpoint. Send the token in the request header. If it is missing there, ASP.NET Core's
antiforgery middleware looks for it in the form, and reading the form buffers the body you meant to stream.
:::
