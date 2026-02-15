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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FileUploadEndpoint.cs#L7-L30' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_file_uploads' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FileUploadEndpoint.cs#L77-L102' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_multipart_upload_endpoints' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Each `IFormFile` parameter is matched to the uploaded file by its parameter name. When sending a multipart request, make sure the form field names match the parameter names in your endpoint method.
