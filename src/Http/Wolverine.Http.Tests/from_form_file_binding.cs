using System.Net.Http;
using Shouldly;

namespace Wolverine.Http.Tests;

public class from_form_file_binding : IntegrationContext
{
    public from_form_file_binding(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task bind_single_file_on_complex_model()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("test-name"), "Name");
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "File", "test.txt");

        var response = await Host.Server.CreateClient().PostAsync("/api/fromform-file", content);
        var text = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();
        text.ShouldBe("test-name|test.txt|3");
    }

    [Fact]
    public async Task bind_file_collection_on_complex_model()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("test-name"), "Name");
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "Files", "file1.txt");
        content.Add(new ByteArrayContent(new byte[] { 4, 5 }), "Files", "file2.txt");

        var response = await Host.Server.CreateClient().PostAsync("/api/fromform-files", content);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldBe("test-name|2");
    }

    [Fact]
    public async Task bind_file_on_complex_model_when_no_file_sent()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("test-name"), "Name");

        var response = await Host.Server.CreateClient().PostAsync("/api/fromform-file", content);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldBe("test-name||");
    }

    [Fact]
    public async Task bind_multiple_named_files()
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "document", "doc.pdf");
        content.Add(new ByteArrayContent(new byte[] { 4, 5 }), "thumbnail", "thumb.jpg");

        var response = await Host.Server.CreateClient().PostAsync("/upload/named-files", content);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldBe("doc.pdf|3|thumb.jpg|2");
    }

    [Fact]
    public async Task bind_fromform_complex_type_with_separate_file()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("My Document"), "Title");
        content.Add(new StringContent("A description"), "Description");
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "file", "test.pdf");

        var response = await Host.Server.CreateClient().PostAsync("/upload/mixed", content);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldBe("My Document|A description|test.pdf|3");
    }

    [Fact]
    public async Task bind_iform_collection()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("value1"), "key1");
        content.Add(new StringContent("value2"), "key2");
        content.Add(new ByteArrayContent(new byte[] { 1 }), "file", "test.txt");

        var response = await Host.Server.CreateClient().PostAsync("/upload/form-collection", content);
        response.EnsureSuccessStatusCode();
        var text = await response.Content.ReadAsStringAsync();
        text.ShouldBe("keys:key1,key2|files:1");
    }
}
