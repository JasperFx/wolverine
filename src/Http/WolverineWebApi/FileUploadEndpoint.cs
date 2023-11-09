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