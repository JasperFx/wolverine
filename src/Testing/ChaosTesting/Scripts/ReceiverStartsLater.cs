using JasperFx.Core;

namespace ChaosTesting.Scripts;

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