using System;
using System.Threading.Tasks;
using CoreTests.Messaging;
using Microsoft.Extensions.Hosting;
using TestingSupport;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class TrackedSessionTester : IDisposable
{
    private readonly IHost _host;
    private readonly Envelope theEnvelope = ObjectMother.Envelope();
    private readonly TrackedSession theSession;


    public TrackedSessionTester()
    {
        _host = WolverineHost.Basic();

        theSession = new TrackedSession(_host);
    }


    public void Dispose()
    {
        _host?.Dispose();
    }

    [Fact]
    public async Task throw_if_any_exceptions_happy_path()
    {
        theSession.Record(MessageEventType.Sent, theEnvelope, "", Guid.NewGuid());
        await theSession.TrackAsync();
        theSession.AssertNoExceptionsWereThrown();
    }

    [Fact]
    public async Task throw_if_any_exceptions_sad_path()
    {
        var guid = Guid.NewGuid();
        theSession.Record(MessageEventType.ExecutionStarted, theEnvelope, "", guid);
        theSession.Record(MessageEventType.ExecutionFinished, theEnvelope, "", guid, new DivideByZeroException());
        await theSession.TrackAsync();

        Should.Throw<AggregateException>(() => theSession.AssertNoExceptionsWereThrown());
    }

    [Fact]
    public async Task throw_if_any_exceptions_and_completed_happy_path()
    {
        var guid = Guid.NewGuid();
        theSession.Record(MessageEventType.ExecutionStarted, theEnvelope, "", guid);
        theSession.Record(MessageEventType.ExecutionFinished, theEnvelope, "", guid);
        await theSession.TrackAsync();
        theSession.AssertNoExceptionsWereThrown();
        theSession.AssertNotTimedOut();
    }

    [Fact]
    public async Task throw_if_any_exceptions_and_completed_sad_path_with_exceptions()
    {
        var guid = Guid.NewGuid();
        theSession.Record(MessageEventType.ExecutionStarted, theEnvelope, "", guid);
        theSession.Record(MessageEventType.ExecutionFinished, theEnvelope, "", guid, new DivideByZeroException());
        await theSession.TrackAsync();

        Should.Throw<AggregateException>(() =>
        {
            theSession.AssertNoExceptionsWereThrown();
            theSession.AssertNotTimedOut();
        });
    }

    [Fact]
    public async Task throw_if_any_exceptions_and_completed_sad_path_with_never_completed()
    {
        theSession.Record(MessageEventType.ExecutionStarted, theEnvelope, "", Guid.NewGuid());
        await theSession.TrackAsync();

        Should.Throw<TimeoutException>(() =>
        {
            theSession.AssertNoExceptionsWereThrown();
            theSession.AssertNotTimedOut();
        });
    }
}