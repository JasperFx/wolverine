using JasperFx.Core;

namespace ChaosTesting.Scripts;

public class ReceiverGoesUpAndDown : ChaosScript
{
    public ReceiverGoesUpAndDown()
    {
        TimeOut = 3.Minutes();
    }

    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartSender("one");
        await driver.SendMessages("one", 1000);

        await driver.StartSender("two");
        driver.SendMessagesContinuously("two", 10, 10.Seconds());
        await Task.Delay(1.Seconds());
        await driver.StartReceiver("one");
        await Task.Delay(3.Seconds());
        await driver.StopSender("one");

        await driver.StartReceiver("two");
    }
}