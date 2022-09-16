using System.Diagnostics;

namespace TestingSupport.Compliance
{
    public class BlueHandler
    {
        public static void Consume(BlueMessage message)
        {
            Debug.WriteLine("Hey");
        }
    }
}
