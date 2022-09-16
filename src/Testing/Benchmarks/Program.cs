using BenchmarkDotNet.Running;

namespace Benchmarks
{
    internal class Program
    {
        private static void Main(string[] args)
        {
            // using var driver = new Driver();
            // driver.Start(o => { }).GetAwaiter().GetResult();
            //
            // driver.Publisher.Invoke(Target.Random()).GetAwaiter().GetResult();

            //var summary = BenchmarkRunner.Run<PersistenceRunner>();
            var summary = BenchmarkRunner.Run<InvokeRunner>();
            //var
            summary = BenchmarkRunner.Run<LocalRunner>();
            //var summary = BenchmarkRunner.Run<RabbitMqRunner>();
        }
    }
}
