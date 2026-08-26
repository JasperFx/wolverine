using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

// GH-4125. DoNotAssertOnExceptionsDetected() used to share its flag with the session's own timeout
// assertion, so a resiliency test -- which reaches for that method for its documented reason, that
// handlers are *expected* to throw -- silently also opted out of the only assertion that would catch
// the session having completed none of its work. The result was a permanently green test over a
// session that did nothing, and because the symptom is a test that never fails rather than one that
// fails oddly, it could sit in a suite indefinitely.
public class timeout_assertion_is_not_suppressed_4125
{
    [Fact]
    public async Task a_timed_out_session_still_throws_with_DoNotAssertOnExceptionsDetected()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Discovery.DisableConventionalDiscovery())
            .StartAsync(TestContext.Current.CancellationToken);

        var ex = await Should.ThrowAsync<TimeoutException>(async () =>
        {
            await host.TrackActivity()
                .Timeout(1.Seconds())
                .DoNotAssertOnExceptionsDetected()
                .WaitForCondition(new NeverSatisfied())
                .ExecuteAndWaitAsync(_ => Task.CompletedTask);
        });

        // The message carries the diagnostics a returned session would have been consulted for
        ex.Message.ShouldContain("timed out before all activity completed");
    }

    [Fact]
    public async Task exceptions_are_still_suppressed_by_the_flag()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(AlwaysThrowsHandler));
            }).StartAsync(TestContext.Current.CancellationToken);

        // Completes normally -- the handler throws, but that is exactly what the flag opts out of
        var session = await host.TrackActivity()
            .DoNotAssertOnExceptionsDetected()
            .SendMessageAndWaitAsync(new AlwaysThrows());

        session.Status.ShouldBe(TrackingStatus.Completed);
        session.AllExceptions().OfType<InvalidOperationException>().Any().ShouldBeTrue();
    }

    public class NeverSatisfied : ITrackedCondition
    {
        public void Record(EnvelopeRecord record)
        {
        }

        public bool IsCompleted()
        {
            return false;
        }
    }
}

public record AlwaysThrows;

public static class AlwaysThrowsHandler
{
    public static void Handle(AlwaysThrows message)
    {
        throw new InvalidOperationException("boom");
    }
}
