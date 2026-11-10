using System.Net.Http;
using Shouldly;

namespace Wolverine.Http.Tests;

/// <summary>
/// <c>Request.Form</c> parses an unread body synchronously, blocking the request thread while a large
/// multipart upload arrives. Every form binding reads one <c>IFormCollection</c> that the generated handler
/// awaits from <c>ReadFormAsync</c> instead.
/// </summary>
public class reading_the_form_asynchronously : IntegrationContext
{
    public reading_the_form_asynchronously(AppFixture fixture) : base(fixture)
    {
    }

    [Theory]
    [InlineData("/form/string")] // single [FromForm] value
    [InlineData("/form/collection/int")] // [FromForm] collection
    [InlineData("/form/intarray")] // [FromForm] array
    [InlineData("/api/fromform1")] // [FromForm] type, constructor
    [InlineData("/api/fromform-file")] // [FromForm] type, value and file properties
    [InlineData("/api/fromform-files")] // [FromForm] type, file collection property
    [InlineData("/api/asparameters1")] // [AsParameters] type with [FromForm] members
    [InlineData("/upload/named-files")] // IFormFile parameters
    [InlineData("/upload/files")] // IFormFileCollection parameter
    [InlineData("/upload/form-collection")] // IFormCollection parameter
    [InlineData("/upload/mixed")] // [FromForm] type and an IFormFile parameter
    public async Task the_handler_awaits_one_form_read(string url)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("1"), "value");
        content.Add(new ByteArrayContent([1, 2, 3]), "file", "file.txt");

        var response = await Host.Server.CreateClient().PostAsync(url, content, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var source = HttpChains.ChainFor("POST", url)!.SourceCode!;

        source.Split("await httpContext.Request.ReadFormAsync(httpContext.RequestAborted)").Length.ShouldBe(2);
        source.ShouldNotContain("Request.Form");
        source.ShouldNotContain(nameof(HttpHandler.ReadFormFileByName));
        source.ShouldNotContain(nameof(HttpHandler.ReadManyFormFileValues));
    }
}
