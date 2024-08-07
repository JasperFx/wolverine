using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace SlowTests;

public class delayed_message_end_to_end
{
    [Fact]
    public async Task receive_timeout_message()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine().StartAsync();

        var message = new KickOffMessage(23);

        var waiter = TimeoutHandler.WaitForEnforcedTimeout(30.Seconds());

        var stopwatch = new Stopwatch();
        stopwatch.Start();

        await host.SendAsync(message);

        var envelope = await waiter;

        stopwatch.Stop();

        // should have been a 3 second delay, and it's not perfect. But at least 3 seconds
        stopwatch.Elapsed.ShouldBeGreaterThan(3.Seconds());

        envelope.Message.ShouldBeOfType<EnforcedTimeout>()
            .Number.ShouldBe(23);
    }
}

public record EnforcedTimeout(int Number) : TimeoutMessage(3.Seconds());

public record KickOffMessage(int Number);

public class TimeoutHandler
{
    private static TaskCompletionSource<Envelope> _completion;

    public static Task<Envelope> WaitForEnforcedTimeout(TimeSpan timeout)
    {
        _completion = new TaskCompletionSource<Envelope>();
        return _completion.Task.WaitAsync(timeout);
    }

    public EnforcedTimeout Handle(KickOffMessage message)
    {
        return new EnforcedTimeout(message.Number);
    }

    public void Handle(EnforcedTimeout message, Envelope envelope)
    {
        _completion?.TrySetResult(envelope);
    }
}