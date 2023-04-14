namespace ChaosTesting.Scripts;

public class Simplistic : ChaosScript
{
    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartReceiver("one");
        await driver.StartSender("one");

        await driver.SendMessages("one", 100);
    }
}