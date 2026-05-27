using Alba;
using Shouldly;
using Wolverine.Tracking;
using WolverineWebApi;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Http.Tests;

// Regression test for https://github.com/JasperFx/wolverine/issues/2917.
// /issue2917 is an EF Core [Transactional] endpoint that returns a cascading Issue2917Message.
// EF Core's Eager transaction middleware now commits the transaction and flushes the outbox (via
// the CommitEfCoreEnvelopeTransaction postprocessor) BEFORE the HTTP response is written. Previously
// the flush happened inside EnrollDbContextInTransaction.CommitAsync at the end of the wrap, AFTER
// WriteJsonAsync wrote the response - so with Alba the client received the response before the
// "sent" envelope was recorded and a TrackActivity session (with no explicit wait condition) could
// finish empty.
public class issue_2917_trackactivity_transactional_outbox : IntegrationContext
{
    private readonly ITestOutputHelper _output;

    public issue_2917_trackactivity_transactional_outbox(AppFixture fixture, ITestOutputHelper output) : base(fixture)
    {
        _output = output;
    }

    [Fact]
    public async Task tracked_session_captures_the_transactional_outbox_message()
    {
        var tracked = await Host
            .TrackActivity()
            .ExecuteAndWaitAsync(_ => Host.Scenario(x => x.Post.Json(new Issue2917Request("test")).ToUrl("/issue2917")));

        _output.WriteLine(tracked.ToString());

        tracked.MessageSucceeded.SingleMessage<Issue2917Message>()
            .Name.ShouldBe("test");
    }

    // An explicit wait condition is no longer required (the test above passes without one), but it
    // must of course still work.
    [Fact]
    public async Task tracked_session_with_explicit_wait_condition()
    {
        var tracked = await Host
            .TrackActivity()
            .WaitForMessageToBeReceivedAt<Issue2917Message>(Host)
            .ExecuteAndWaitAsync(_ => Host.Scenario(x => x.Post.Json(new Issue2917Request("test")).ToUrl("/issue2917")));

        tracked.MessageSucceeded.SingleMessage<Issue2917Message>()
            .Name.ShouldBe("test");
    }
}
