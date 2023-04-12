using JasperFx.Core;

namespace ChaosTesting.Scripts;

public class Simplistic : ChaosScript
{
    public Simplistic() : base("Simple", 120.Seconds())
    {
    }

    public override async Task Drive(ChaosDriver driver)
    {
        await driver.StartReceiver("one");
        await driver.StartSender("one");

        await driver.SendMessages("one", 1000);
    }
}