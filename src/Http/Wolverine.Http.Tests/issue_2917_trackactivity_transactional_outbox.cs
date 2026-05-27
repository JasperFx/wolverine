using Alba;
using Shouldly;
using Wolverine.Tracking;
using WolverineWebApi;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Http.Tests;

// Reproduction for https://github.com/JasperFx/wolverine/issues/2917.
// /issue2917 is [Transactional] (Marten outbox) and returns a cascading Issue2917Message.
// Under the outbox that message is only sent in FlushOutgoingMessagesAsync, which the generated
// endpoint runs AFTER WriteJsonAsync. With Alba the client receives the response before the flush
// records the "sent" envelope, so a TrackActivity session that relies on the default
// "wait until activity quiesces" behavior can finish empty.
public class issue_2917_trackactivity_transactional_outbox : IntegrationContext
{
    private readonly ITestOutputHelper _output;

    public issue_2917_trackactivity_transactional_outbox(AppFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public async Task tracked_session_should_capture_the_transactional_outbox_message()
    {
        var tracked = await Host
            .TrackActivity()
            .ExecuteAndWaitAsync(_ => Host.Scenario(x => x.Post.Json(new Issue2917Request("test")).ToUrl("/issue2917")));

        _output.WriteLine(tracked.ToString());

        tracked.MessageSucceeded.SingleMessage<Issue2917Message>()
            .Name.ShouldBe("test");
    }

    // Demonstrates the current workaround: an explicit wait condition keeps the tracked session
    // open until the cascading message is actually received, past the HTTP response. If this PASSES
    // while the test above FAILS, it proves the message IS sent — just after the response is
    // written (and after ExecuteAndWaitAsync's default quiescence check has already returned).
    [Fact]
    public async Task workaround_with_explicit_wait_condition()
    {
        var tracked = await Host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<Issue2917Message>(Host)
            .ExecuteAndWaitAsync(_ => Host.Scenario(x => x.Post.Json(new Issue2917Request("test")).ToUrl("/issue2917")));

        tracked.MessageSucceeded.SingleMessage<Issue2917Message>()
            .Name.ShouldBe("test");
    }
}
