using System;
using System.Threading.Tasks;
using CoreTests.Messaging;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using TestingSupport;
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
    [InlineData(new[] { MessageEventType.NoRoutes }, true)]
    [InlineData(new[] { MessageEventType.Sent, MessageEventType.NoRoutes }, true)]
    [InlineData(new[] { MessageEventType.Received }, false)]
    [InlineData(new[] { MessageEventType.Received, MessageEventType.ExecutionStarted }, false)]
    [InlineData(new[] { MessageEventType.Received, MessageEventType.ExecutionStarted, MessageEventType.ExecutionFinished }, false)]
    [InlineData(
        new[] { MessageEventType.Received, MessageEventType.ExecutionStarted, MessageEventType.ExecutionFinished, MessageEventType.MessageFailed },
        true)]
    [InlineData(
        new[]
        {
            MessageEventType.Received, MessageEventType.ExecutionStarted, MessageEventType.ExecutionFinished, MessageEventType.MessageSucceeded
        }, true)]
    public void envelope_history_determining_when_complete_locally(MessageEventType[] events, bool isComplete)
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

        history.RecordLocally(new EnvelopeRecord(MessageEventType.Sent, env1, 110, null));
        history.IsComplete().ShouldBeFalse();
    }

    [Fact]
    public void sending_an_envelope_that_is_not_local_does_finish_a_locally_tracked_session()
    {
        var history = new EnvelopeHistory(env1.Id);
        env1.Destination = "tcp://localhost:4444".ToUri();


        history.RecordLocally(new EnvelopeRecord(MessageEventType.Sent, env1, 110, null));
        history.IsComplete().ShouldBeTrue();
    }

    [Theory]
    [InlineData(new[] { MessageEventType.Sent }, false)]
    [InlineData(new[] { MessageEventType.Received }, false)]
    [InlineData(new[] { MessageEventType.Received, MessageEventType.ExecutionStarted }, false)]
    [InlineData(new[] { MessageEventType.Received, MessageEventType.ExecutionStarted, MessageEventType.ExecutionFinished }, false)]
    [InlineData(
        new[] { MessageEventType.Received, MessageEventType.ExecutionStarted, MessageEventType.ExecutionFinished, MessageEventType.MessageFailed },
        true)]
    [InlineData(
        new[]
        {
            MessageEventType.Received, MessageEventType.ExecutionStarted, MessageEventType.ExecutionFinished, MessageEventType.MessageSucceeded
        }, true)]
    [InlineData(new[] { MessageEventType.NoRoutes }, true)]
    [InlineData(new[] { MessageEventType.Sent, MessageEventType.NoRoutes }, true)]
    public void envelope_history_determining_when_complete_cross_app(MessageEventType[] events, bool isComplete)
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

        var guid = Guid.NewGuid();

        session.Record(MessageEventType.Received, env1, "wolverine", guid);
        session.Record(MessageEventType.ExecutionStarted, env1, "wolverine", guid);
        session.Record(MessageEventType.ExecutionFinished, env1, "wolverine", guid);

        session.Status.ShouldBe(TrackingStatus.Active);

        session.Record(MessageEventType.MessageSucceeded, env1, "wolverine", guid);

        await session.TrackAsync();

        session.Status.ShouldBe(TrackingStatus.Completed);
    }

    [Fact]
    public async Task multiple_active_envelopes()
    {
        var session = new TrackedSession(_host);

        var guid = Guid.NewGuid();

        session.Record(MessageEventType.Received, env1, "wolverine", guid);
        session.Record(MessageEventType.ExecutionStarted, env1, "wolverine", guid);
        session.Record(MessageEventType.ExecutionFinished, env1, "wolverine", guid);

        session.Status.ShouldBe(TrackingStatus.Active);

        session.Record(MessageEventType.Received, env2, "wolverine", guid);
        session.Record(MessageEventType.ExecutionStarted, env2, "wolverine", guid);
        session.Record(MessageEventType.ExecutionFinished, env2, "wolverine", guid);

        session.Record(MessageEventType.MessageSucceeded, env1, "wolverine", guid);

        session.Status.ShouldBe(TrackingStatus.Active);

        session.Record(MessageEventType.MessageSucceeded, env2, "wolverine", guid);

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