using JasperFx.Core;
using JasperFx.Events;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class recording_appended_events_while_a_session_ends_5683
{
    // GH-5683. A tracked session on a shared host can end on another thread while an event store session commits
    // outside of any message. Recording those events must never throw out of the application's SaveChangesAsync()
    [Fact]
    public async Task recording_events_committed_outside_a_message_survives_the_tracked_session_ending()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery())
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        var session = new TrackedSession(host);
        var streams = new List<StreamAction> { new(Guid.NewGuid(), StreamActionType.Append) };

        using var stopping = new CancellationTokenSource(2.Seconds());

        var sessionsComingAndGoing = Task.Run(() =>
        {
            while (!stopping.IsCancellationRequested)
            {
                runtime.ActiveSession = session;
                runtime.ActiveSession = null;
            }
        }, TestContext.Current.CancellationToken);

        Exception? failure = null;
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                runtime.RecordAppendedEventsOutsideAMessage(streams, null, null);
            }
        }
        catch (Exception e)
        {
            failure = e;
            await stopping.CancelAsync();
        }

        await sessionsComingAndGoing;
        runtime.ActiveSession = null;

        failure.ShouldBeNull();
    }
}
