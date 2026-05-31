using Alba;
using Shouldly;

namespace Wolverine.Http.Tests.Streaming;

public class StreamingTests : IntegrationContext
{
    public StreamingTests(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task can_stream_sse_events()
    {
        var result = await Scenario(s =>
        {
            s.Get.Url("/api/sse/events");
            s.StatusCodeShouldBeOk();
            s.Header("content-type").SingleValueShouldEqual("text/event-stream");
        });

        var body = await result.ReadAsTextAsync();
        body.ShouldContain("data: Event 0");
        body.ShouldContain("data: Event 2");
    }

    [Fact]
    public async Task can_stream_plain_text()
    {
        var result = await Scenario(s =>
        {
            s.Get.Url("/api/stream/data");
            s.StatusCodeShouldBeOk();
            s.Header("content-type").SingleValueShouldEqual("text/plain");
        });

        var body = await result.ReadAsTextAsync();
        body.ShouldContain("line 0");
        body.ShouldContain("line 4");
    }
}
