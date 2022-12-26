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
        theSession.Record(EventType.Sent, theEnvelope, "", 1);
        await theSession.TrackAsync();
        theSession.AssertNoExceptionsWereThrown();
    }

    [Fact]
    public async Task throw_if_any_exceptions_sad_path()
    {
        theSession.Record(EventType.ExecutionStarted, theEnvelope, "", 1);
        theSession.Record(EventType.ExecutionFinished, theEnvelope, "", 1, new DivideByZeroException());
        await theSession.TrackAsync();

        Should.Throw<AggregateException>(() => theSession.AssertNoExceptionsWereThrown());
    }

    [Fact]
    public async Task throw_if_any_exceptions_and_completed_happy_path()
    {
        theSession.Record(EventType.ExecutionStarted, theEnvelope, "", 1);
        theSession.Record(EventType.ExecutionFinished, theEnvelope, "", 1);
        await theSession.TrackAsync();
        theSession.AssertNoExceptionsWereThrown();
        theSession.AssertNotTimedOut();
    }

    [Fact]
    public async Task throw_if_any_exceptions_and_completed_sad_path_with_exceptions()
    {
        theSession.Record(EventType.ExecutionStarted, theEnvelope, "", 1);
        theSession.Record(EventType.ExecutionFinished, theEnvelope, "", 1, new DivideByZeroException());
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
        theSession.Record(EventType.ExecutionStarted, theEnvelope, "", 1);
        await theSession.TrackAsync();

        Should.Throw<TimeoutException>(() =>
        {
            theSession.AssertNoExceptionsWereThrown();
            theSession.AssertNotTimedOut();
        });
    }
}