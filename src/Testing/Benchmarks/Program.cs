using BenchmarkDotNet.Running;

namespace Benchmarks;

internal class Program
{
    private static void Main(string[] args)
    {
        // e.g. dotnet run -c Release -f net9.0 -- --job short --filter '*KafkaHotPath*'
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
    }
}
