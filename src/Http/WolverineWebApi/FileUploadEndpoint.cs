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