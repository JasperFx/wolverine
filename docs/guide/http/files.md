# Uploading Files

As of 1.11.0, Wolverine supports file uploads through the standard ASP.Net Core `IFile` or `IFileCollection` types. All you need 
to do to is to have an input parameter to your Wolverine.HTTP endpoint of these types like so:

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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/FileUploadEndpoint.cs#L6-L29' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_file_uploads' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

See [Upload files in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-7.0)
for more information about these types. 
