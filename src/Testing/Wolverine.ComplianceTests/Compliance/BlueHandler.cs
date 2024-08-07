using System.Diagnostics;

namespace Wolverine.ComplianceTests.Compliance;

public class BlueHandler
{
    public static void Consume(BlueMessage message)
    {
        Debug.WriteLine("Hey");
    }
}