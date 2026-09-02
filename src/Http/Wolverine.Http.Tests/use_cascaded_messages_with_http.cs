using Wolverine.Tracking;
using Shouldly;
using WolverineWebApi;

namespace Wolverine.Http.Tests;

public class use_cascaded_messages_with_http : IntegrationContext
{
    public use_cascaded_messages_with_http(AppFixture fixture) : base(fixture)
    {
    }

    // GH-4248: every IntegrationContext test shares one host, and a tracked session observes ALL
    // message activity on that host rather than only what its own HTTP call caused. dead_letter_endpoints
    // deliberately publishes MessageThatAlwaysGoesToDeadLetter, whose handler always throws; that class
    // guards its own sessions with DoNotAssertOnExceptionsDetected(), but the exception was still landing
    // inside whatever OTHER session happened to be open, and failing it. Both tests below were observed
    // failing that way on unchanged code, with the foreign exception's own message ("replayable-{Guid}",
    // minted in dead_letter_endpoints) visible in the failure.
    //
    // Ignoring that one message type is enough: TrackedSession.Record() returns before it attaches the
    // exception when the message type is ignored. Deliberately narrow -- these tests still assert on
    // exceptions from everything else, which is the point of using a tracked session at all.
    private static TrackedSessionConfiguration ignoreForeignDeadLetters(TrackedSessionConfiguration config)
        => config.IgnoreMessageType<MessageThatAlwaysGoesToDeadLetter>();

    [Fact]
    public async Task send_cascaded_messages_from_tuple_response()
    {
        // This would fail if the status code != 200 btw
        // This method waits until *all* detectable Wolverine message
        // processing has completed
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Json(new SpawnInput("Chris Jones")).ToUrl("/spawn");
        }, ignoreForeignDeadLetters);

        var text = await result.ReadAsTextAsync();
        text.ShouldBe("got it");

        // "tracked" is a Wolverine ITrackedSession object that lets us interrogate
        // what messages were published, sent, and handled during the testing perioc
        tracked.Sent.SingleMessage<HttpMessage1>().Name.ShouldBe("Chris Jones");
        tracked.Sent.SingleMessage<HttpMessage2>().Name.ShouldBe("Chris Jones");
        tracked.Sent.SingleMessage<HttpMessage3>().Name.ShouldBe("Chris Jones");
        tracked.Sent.SingleMessage<HttpMessage4>().Name.ShouldBe("Chris Jones");
    }

    [Fact]
    public async Task no_content_chains_should_use_cascading_messages_for_create_variables()
    {
        var (tracked, result) = await TrackedHttpCall(x =>
        {
            x.Post.Url("/spawn2");
            x.StatusCodeShouldBe(204);
        }, ignoreForeignDeadLetters);

        tracked.Sent.SingleMessage<HttpMessage1>().ShouldNotBeNull();
        tracked.Sent.SingleMessage<HttpMessage2>().ShouldNotBeNull();
    }
}
