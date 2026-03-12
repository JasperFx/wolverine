using JasperFx.Core;

namespace ChaosTesting.Scripts;

/// <summary>
/// Tests that in-flight messages complete during graceful shutdown and no messages
/// are left orphaned. Sends messages, then stops the receiver while messages are
/// still being processed, starts a new receiver, and verifies all messages complete.
/// </summary>
public class GracefulShutdown : ChaosScript
{
    public GracefulShutdown()
    {
        TimeOut = 2.Minutes();
    }

    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartReceiver("one");
        await driver.StartSender("one");

        // Send a batch of messages
        await driver.SendMessages("one", 200);

        // Give some time for processing to start but not complete
        await Task.Delay(500.Milliseconds());

        // Gracefully stop the receiver while messages are in-flight
        await driver.StopReceiver("one");

        // Start a new receiver to pick up any remaining messages
        await driver.StartReceiver("two");
    }
}

/// <summary>
/// Tests that multiple rapid shutdown/restart cycles don't lose messages.
/// Simulates rolling deployment behavior where receivers are stopped and new
/// ones started in quick succession.
/// </summary>
public class RollingRestart : ChaosScript
{
    public RollingRestart()
    {
        TimeOut = 3.Minutes();
    }

    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartReceiver("one");
        await driver.StartSender("one");

        // Send initial batch
        await driver.SendMessages("one", 300);

        // Simulate a rolling restart: stop old, start new, repeat
        await Task.Delay(1.Seconds());
        await driver.StopReceiver("one");

        await driver.StartReceiver("two");
        await Task.Delay(1.Seconds());
        await driver.StopReceiver("two");

        await driver.StartReceiver("three");
    }
}
