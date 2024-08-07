using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
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
    public async Task throw_if_any_exceptions_and_completed_sad_path_with_exceptions()
    {
        var guid = Guid.NewGuid();
        theSession.Record(MessageEventType.ExecutionStarted, theEnvelope, "", guid);
        theSession.Record(MessageEventType.ExecutionFinished, theEnvelope, "", guid, new DivideByZeroException());

        await Should.ThrowAsync<AggregateException>(async () =>
        {
            await theSession.TrackAsync();
        });
    }
}