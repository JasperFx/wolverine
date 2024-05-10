using Baseline.Dates;
using OtelMessages;
using Shouldly;
using TracingTests;
using Wolverine;
using Wolverine.Tracking;
using Xunit.Abstractions;

[Collection("otel")]
public class correlation_tracing : IClassFixture<HostsFixture>, IAsyncLifetime
{
    private readonly HostsFixture _fixture;
    private readonly ITestOutputHelper _output;
    private Envelope theOriginalEnvelope;
    private ITrackedSession theSession;

    public correlation_tracing(HostsFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    public async Task InitializeAsync()
    {
        theSession = await _fixture.WebApi
            .TrackActivity()
            .AlsoTrack(_fixture.FirstSubscriber)
            .AlsoTrack(_fixture.SecondSubscriber)
            .Timeout(1.Minutes())
            .ExecuteAndWaitAsync(c =>
            {
                return _fixture.WebApi.Scenario(x =>
                {
                    x.Post.Json(new InitialPost("Byron Scott")).ToUrl("/invoke");
                });
            });

        foreach (var record in theSession.AllRecordsInOrder().Where(x => x.MessageEventType == MessageEventType.MessageSucceeded))
            _output.WriteLine(record.ToString());

        theOriginalEnvelope = theSession.Executed.SingleEnvelope<InitialCommand>();
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public void can_find_the_initial_command()
    {
        theOriginalEnvelope.ShouldNotBeNull();
    }

    [Fact]
    public void tracing_from_invoke_to_other_invoke()
    {
        var envelope = theSession.Executed.SingleEnvelope<LocalMessage1>();

        envelope.CorrelationId.ShouldBe(theOriginalEnvelope.CorrelationId);
        envelope.Source.ShouldBe("OtelWebApi");
        envelope.ConversationId.ShouldBe(theOriginalEnvelope.Id);
    }

    [Fact]
    public void tracing_from_invoke_to_enqueue()
    {
        var envelope = theSession.Executed.SingleEnvelope<LocalMessage2>();

        envelope.CorrelationId.ShouldBe(theOriginalEnvelope.CorrelationId);
        envelope.Source.ShouldBe("OtelWebApi");
        envelope.ConversationId.ShouldBe(theOriginalEnvelope.Id);
    }

    [Fact]
    public void trace_through_tcp()
    {
        var envelope = theSession.Received.SingleEnvelope<TcpMessage1>();

        envelope.CorrelationId.ShouldBe(theOriginalEnvelope.CorrelationId);
        envelope.Source.ShouldBe("OtelWebApi");
        envelope.ConversationId.ShouldBe(theOriginalEnvelope.Id);
    }

    [Fact]
    public void trace_through_rabbit()
    {
        var envelopes = theSession.FindEnvelopesWithMessageType<RabbitMessage1>()
            .Where(x => x.MessageEventType == MessageEventType.MessageSucceeded)
            .Select(x => x.Envelope)
            .OrderBy(x => x.Source)
            .ToArray();

        var atSubscriber1 = envelopes[0];
        var atSubscriber2 = envelopes[1];

        atSubscriber1.Source.ShouldBe("OtelWebApi");
        atSubscriber2.Source.ShouldBe("OtelWebApi");

        atSubscriber1.CorrelationId.ShouldBe(theOriginalEnvelope.CorrelationId);
        atSubscriber2.CorrelationId.ShouldBe(theOriginalEnvelope.CorrelationId);

        atSubscriber1.ConversationId.ShouldBe(theOriginalEnvelope.Id);
        atSubscriber2.ConversationId.ShouldBe(theOriginalEnvelope.Id);
    }
}