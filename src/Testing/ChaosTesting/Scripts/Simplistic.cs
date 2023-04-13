using JasperFx.Core;

namespace ChaosTesting.Scripts;

public class Simplistic : ChaosScript
{
    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartReceiver("one");
        await driver.StartSender("one");

        await driver.SendMessages("one", 1000);
    }
}

public class ReceiverStartsLater : ChaosScript
{
    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartSender("one");
        await driver.SendMessages("one", 1000);
        await Task.Delay(1.Seconds());
        await driver.StartReceiver("one");
    }
}

public class ReceiverGoesUpAndDown : ChaosScript
{
    public ReceiverGoesUpAndDown()
    {
        TimeOut = 1.Minutes();
    }

    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartSender("one");
        await driver.SendMessages("one", 1000);

        await driver.StartSender("two");
        driver.SendMessagesContinuously("two", 100, 10.Seconds());
        await Task.Delay(1.Seconds());
        await driver.StartReceiver("one");
        await Task.Delay(3.Seconds());
        await driver.StopSender("one");

        await driver.StartReceiver("two");
    }
}