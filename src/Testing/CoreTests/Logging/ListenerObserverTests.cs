using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Wolverine.Logging;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Logging;

public class ListenerObserverTests
{
    private readonly ListenerTracker theTracker = new(NullLogger.Instance);

    [Fact]
    public void initial_state_by_endpoint_name_is_unknown()
    {
        theTracker.StatusFor("foo")
            .ShouldBe(ListeningStatus.Unknown);
    }

    [Fact]
    public void initial_state_by_uri_is_unknown()
    {
        theTracker.StatusFor(TransportConstants.LocalUri)
            .ShouldBe(ListeningStatus.Unknown);
    }

    [Fact]
    public async Task record_status()
    {
        var waiter = theTracker.WaitForListenerStatusAsync(TransportConstants.LocalUri, ListeningStatus.Accepting,
            10.Seconds());

        theTracker.Publish(new ListenerState(TransportConstants.LocalUri, "DefaultLocal", ListeningStatus.Accepting));

        await waiter;

        theTracker.StatusFor(TransportConstants.LocalUri)
            .ShouldBe(ListeningStatus.Accepting);
    }

    [Fact]
    public async Task record_status_and_wait_by_endpoint_name()
    {
        var waiter = theTracker.WaitForListenerStatusAsync("local", ListeningStatus.Accepting,
            10.Seconds());

        theTracker.Publish(new ListenerState(TransportConstants.LocalUri, "local", ListeningStatus.Accepting));

        await waiter;

        theTracker.StatusFor("local")
            .ShouldBe(ListeningStatus.Accepting);
    }
}