using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_using_file_uploads

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

#endregion

public static class Bug748Endpoint
{
    [WolverinePost("/upload/sideeffect")]
    public static (SomeSideEffect, OutgoingMessages) Upload(IFormFile file)
    {
        return (new SomeSideEffect(), []);
    }
}

public static class Bug928Endpoint
{
    [Middleware(typeof(Bug928MiddleWare.FileLengthValidationMiddleware))]
    [WolverinePost("/upload/middleware")]
    public static Task HandleAsync(IFormFile file)
    {
        // Process file
        return Task.CompletedTask;
    }
}

public static class Bug928MiddleWare
{
    public static class FileLengthValidationMiddleware
    {
        public static void Before(IFormFile file)
        {
            // todo, return ProblemDetail if validation fails
        }
    }
}

public class SomeSideEffect : ISideEffect
{
    public static bool WasExecuted = false;

    public void Execute()
    {
        WasExecuted = true;
    }
}

public record UploadMetadata(string Title, string Description);

public static class MultipartUploadEndpoints
{
    [WolverinePost("/upload/named-files")]
    public static string UploadNamedFiles(IFormFile document, IFormFile thumbnail)
    {
        return $"{document?.FileName}|{document?.Length}|{thumbnail?.FileName}|{thumbnail?.Length}";
    }

    [WolverinePost("/upload/mixed")]
    public static string UploadMixed([FromForm] UploadMetadata metadata, IFormFile file)
    {
        return $"{metadata.Title}|{metadata.Description}|{file?.FileName}|{file?.Length}";
    }

    [WolverinePost("/upload/form-collection")]
    public static string UploadFormCollection(IFormCollection form)
    {
        var keys = string.Join(",", form.Keys.OrderBy(k => k));
        var fileCount = form.Files.Count;
        return $"keys:{keys}|files:{fileCount}";
    }
}