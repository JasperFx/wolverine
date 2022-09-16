using System;
using System.Threading.Tasks;
using Baseline.Dates;
using CoreTests.Messaging;
using Microsoft.Extensions.Hosting;
using Shouldly;
using TestingSupport;
using Wolverine;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace CoreTests.Tracking;

public class when_determining_if_the_session_is_done : IDisposable
{
    private readonly IHost _host;
    private readonly Envelope env1 = ObjectMother.Envelope();
    private readonly Envelope env2 = ObjectMother.Envelope();
    private readonly Envelope env3 = ObjectMother.Envelope();
    private readonly Envelope env4 = ObjectMother.Envelope();
    private readonly Envelope env5 = ObjectMother.Envelope();

    private readonly TrackedSession theSession;

    public when_determining_if_the_session_is_done()
    {
        _host = WolverineHost.Basic();
        theSession = new TrackedSession(_host);
    }

    public void Dispose()
    {
        _host?.Dispose();
    }

    [Theory]
    [InlineData(new[] { EventType.NoRoutes }, true)]
    [InlineData(new[] { EventType.Sent, EventType.NoRoutes }, true)]
    [InlineData(new[] { EventType.Received }, false)]
    [InlineData(new[] { EventType.Received, EventType.ExecutionStarted }, false)]
    [InlineData(new[] { EventType.Received, EventType.ExecutionStarted, EventType.ExecutionFinished }, false)]
    [InlineData(
        new[] { EventType.Received, EventType.ExecutionStarted, EventType.ExecutionFinished, EventType.MessageFailed },
        true)]
    [InlineData(
        new[]
        {
            EventType.Received, EventType.ExecutionStarted, EventType.ExecutionFinished, EventType.MessageSucceeded
        }, true)]
    public void envelope_history_determining_when_complete_locally(EventType[] events, bool isComplete)
    {
        var time = 100;
        var history = new EnvelopeHistory(env1.Id);

        foreach (var eventType in events)
        {
            var record = new EnvelopeRecord(eventType, env1, ++time, null);
            history.RecordLocally(record);
        }

        history.IsComplete().ShouldBe(isComplete);
    }

    [Fact]
    public void sending_an_envelope_that_is_local_does_not_finish_a_locally_tracked_session()
    {
        var history = new EnvelopeHistory(env1.Id);

        env1.Destination.Scheme.ShouldBe(TransportConstants.Local);

        history.RecordLocally(new EnvelopeRecord(EventType.Sent, env1, 110, null));
        history.IsComplete().ShouldBeFalse();
    }

    [Fact]
    public void sending_an_envelope_that_is_not_local_does_finish_a_locally_tracked_session()
    {
        var history = new EnvelopeHistory(env1.Id);
        env1.Destination = "tcp://localhost:4444".ToUri();


        history.RecordLocally(new EnvelopeRecord(EventType.Sent, env1, 110, null));
        history.IsComplete().ShouldBeTrue();
    }

    [Theory]
    [InlineData(new[] { EventType.Sent }, false)]
    [InlineData(new[] { EventType.Received }, false)]
    [InlineData(new[] { EventType.Received, EventType.ExecutionStarted }, false)]
    [InlineData(new[] { EventType.Received, EventType.ExecutionStarted, EventType.ExecutionFinished }, false)]
    [InlineData(
        new[] { EventType.Received, EventType.ExecutionStarted, EventType.ExecutionFinished, EventType.MessageFailed },
        true)]
    [InlineData(
        new[]
        {
            EventType.Received, EventType.ExecutionStarted, EventType.ExecutionFinished, EventType.MessageSucceeded
        }, true)]
    [InlineData(new[] { EventType.NoRoutes }, true)]
    [InlineData(new[] { EventType.Sent, EventType.NoRoutes }, true)]
    public void envelope_history_determining_when_complete_cross_app(EventType[] events, bool isComplete)
    {
        var time = 100;
        var history = new EnvelopeHistory(env1.Id);
        foreach (var eventType in events) history.RecordLocally(new EnvelopeRecord(eventType, env1, ++time, null));

        history.IsComplete().ShouldBe(isComplete);
    }

    [Fact]
    public async Task complete_with_one_message()
    {
        var session = new TrackedSession(_host);


        session.Record(EventType.Received, env1, "wolverine", 1);
        session.Record(EventType.ExecutionStarted, env1, "wolverine", 1);
        session.Record(EventType.ExecutionFinished, env1, "wolverine", 1);

        session.Status.ShouldBe(TrackingStatus.Active);

        session.Record(EventType.MessageSucceeded, env1, "wolverine", 1);

        await session.TrackAsync();

        session.Status.ShouldBe(TrackingStatus.Completed);
    }

    [Fact]
    public async Task multiple_active_envelopes()
    {
        var session = new TrackedSession(_host);

        session.Record(EventType.Received, env1, "wolverine", 1);
        session.Record(EventType.ExecutionStarted, env1, "wolverine", 1);
        session.Record(EventType.ExecutionFinished, env1, "wolverine", 1);

        session.Status.ShouldBe(TrackingStatus.Active);

        session.Record(EventType.Received, env2, "wolverine", 1);
        session.Record(EventType.ExecutionStarted, env2, "wolverine", 1);
        session.Record(EventType.ExecutionFinished, env2, "wolverine", 1);

        session.Record(EventType.MessageSucceeded, env1, "wolverine", 1);

        session.Status.ShouldBe(TrackingStatus.Active);

        session.Record(EventType.MessageSucceeded, env2, "wolverine", 1);

        await session.TrackAsync();

        session.Status.ShouldBe(TrackingStatus.Completed);
    }

    [Fact]
    public async Task timeout_session()
    {
        var session = new TrackedSession(_host)
        {
            Timeout = 10.Milliseconds()
        };
        await session.TrackAsync();

        session.Status.ShouldBe(TrackingStatus.TimedOut);
    }
}
