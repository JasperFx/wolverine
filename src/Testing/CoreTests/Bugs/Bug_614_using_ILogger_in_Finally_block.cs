using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Wolverine.Attributes;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Bugs;

public class Bug_614_using_ILogger_in_Finally_block : IntegrationContext
{
    public Bug_614_using_ILogger_in_Finally_block(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task should_be_able_to_execute_and_use_logger_in_finally_method()
    {
        await Host.InvokeMessageAndWaitAsync(new PotentiallySlowMessage("Hey"));
    }
}

public class StopwatchMiddleware
{
    private readonly Stopwatch _stopwatch = new();

    public void Before()
    {
        _stopwatch.Start();
    }

    public void Finally(ILogger logger, Envelope envelope)
    {
        _stopwatch.Stop();
        logger.LogDebug("Envelope {Id} / {MessageType} ran in {Duration} milliseconds",
            envelope.Id, envelope.MessageType, _stopwatch.ElapsedMilliseconds);
    }
}

public record PotentiallySlowMessage(string Name);

public static class SomeHandler
{
    [Middleware(typeof(StopwatchMiddleware))]
    public static void Handle(PotentiallySlowMessage message)
    {
        // do something expensive with the message
    }
}
