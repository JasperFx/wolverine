# Uploading Files

As of 1.11.0, Wolverine supports file uploads through the standard ASP.Net Core `IFile` or `IFileCollection` types. All you need 
to do to is to have an input parameter to your Wolverine.HTTP endpoint of these types like so:

snippet: sample_using_file_uploads

See [Upload files in ASP.NET Core](https://learn.microsoft.com/en-us/aspnet/core/mvc/models/file-uploads?view=aspnetcore-7.0)
for more information about these types. 