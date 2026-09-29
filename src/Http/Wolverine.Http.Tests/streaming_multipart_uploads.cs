using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using JasperFx;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Attributes;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class streaming_multipart_uploads : IntegrationContext
{
    public streaming_multipart_uploads(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task reads_every_section_in_order()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("Quarterly report"), "title");
        content.Add(new ByteArrayContent(new byte[100_000]), "document", "report.pdf");
        content.Add(new ByteArrayContent(new byte[] { 1, 2, 3 }), "thumbnail", "thumb.jpg");

        var response = await Host.Server.CreateClient()
            .PostAsync("/upload/stream", content, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        text.ShouldBe("title=Quarterly report|document:report.pdf:100000|thumbnail:thumb.jpg:3");
    }

    [Fact]
    public async Task never_buffers_the_form()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("a"), "first");
        content.Add(new ByteArrayContent(new byte[] { 1 }), "file", "file.bin");

        var response = await Host.Server.CreateClient()
            .PostAsync("/upload/stream/unbuffered", content, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("2|True");
    }

    [Fact]
    public async Task applies_the_endpoints_request_form_limits_to_the_reader()
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent("a"), "first");

        var response = await Host.Server.CreateClient()
            .PostAsync("/upload/stream/limits", content, TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).ShouldBe("1234|7");
    }

    [Fact]
    public async Task refuses_a_request_that_is_not_multipart()
    {
        var response = await Host.Server.CreateClient().PostAsync("/upload/stream",
            new StringContent("{}", null, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task refuses_a_multipart_request_without_a_boundary()
    {
        var content = new ByteArrayContent(new byte[] { 1, 2, 3 });
        content.Headers.ContentType = new MediaTypeHeaderValue("multipart/form-data");

        var response = await Host.Server.CreateClient()
            .PostAsync("/upload/stream", content, TestContext.Current.CancellationToken);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .ShouldContain("no boundary");
    }

    [Fact]
    public void advertises_multipart_form_data_and_keeps_the_size_limit_attributes()
    {
        var chain = HttpChains.ChainFor("POST", "/upload/stream")!;
        var metadata = chain.Endpoint!.Metadata;

        metadata.GetMetadata<IAcceptsMetadata>()!.ContentTypes.ShouldBe(["multipart/form-data"]);
        metadata.GetMetadata<IRequestSizeLimitMetadata>()!.MaxRequestBodySize.ShouldBeNull();
        metadata.GetMetadata<IFormOptionsMetadata>()!.MultipartBodyLengthLimit.ShouldBe(long.MaxValue);
    }
}

public class streaming_multipart_upload_guards
{
    [Fact]
    public void refuses_a_second_reader_of_the_body()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<MultipartReaderAndFormFile>(x => x.Post(null!, null!)));

        ex.Message.ShouldContain("file parameter 'file'");
        ex.Message.ShouldContain("can only be read once");
    }

    [Fact]
    public void requires_antiforgery_like_any_other_form_endpoint()
    {
        var registry = new ServiceCollection();
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IServiceCollection>(registry);
        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();
        var graph = new HttpGraph(new WolverineOptions(), container) { AutoAntiforgeryOnFormEndpoints = true };

        var chain = HttpChain.ChainFor(typeof(StreamingUploadProbeEndpoints),
            nameof(StreamingUploadProbeEndpoints.Limits), graph);

        chain.BuildEndpoint(RouteWarmup.Lazy).Metadata.GetMetadata<IAntiforgeryMetadata>()!
            .RequiresValidation.ShouldBeTrue();
    }

    [Fact]
    public void refuses_a_get()
    {
        var ex = Should.Throw<InvalidOperationException>(() =>
            HttpChain.ChainFor<MultipartReaderOnGet>(x => x.Get(null!)));

        ex.Message.ShouldContain("would return 404");
    }
}

public class MultipartReaderAndFormFile
{
    [WolverineIgnore]
    [WolverinePost("/upload/stream/and-file")]
    public string Post(MultipartReader reader, IFormFile file) => "nope";
}

public class MultipartReaderOnGet
{
    [WolverineIgnore]
    [WolverineGet("/upload/stream/get")]
    public string Get(MultipartReader reader) => "nope";
}
