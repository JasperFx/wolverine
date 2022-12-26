using BenchmarkDotNet.Attributes;
using JasperFx.Core;

namespace Benchmarks;

[MemoryDiagnoser]
public class InvokeRunner : IDisposable
{
    private readonly Driver theDriver;

    public InvokeRunner()
    {
        theDriver = new Driver();
    }

    public void Dispose()
    {
        theDriver.SafeDispose();
    }

    [IterationSetup]
    public void BuildDatabase()
    {
        theDriver.Start(opts => { opts.Advanced.DurabilityAgentEnabled = false; }).GetAwaiter().GetResult();
    }

    [Benchmark]
    public async Task Invoke()
    {
        for (var i = 0; i < 1000; i++)
        {
            foreach (var target in theDriver.Targets) await theDriver.Bus.InvokeAsync(target);
        }
    }


    [Benchmark]
    public async Task InvokeMultiThreaded()
    {
        var task1 = Task.Factory.StartNew(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                foreach (var target in theDriver.Targets.Take(200)) await theDriver.Bus.InvokeAsync(target);
            }
        });

        var task2 = Task.Factory.StartNew(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                foreach (var target in theDriver.Targets.Skip(200).Take(200))
                    await theDriver.Bus.InvokeAsync(target);
            }
        });

        var task3 = Task.Factory.StartNew(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                foreach (var target in theDriver.Targets.Skip(400).Take(200))
                    await theDriver.Bus.InvokeAsync(target);
            }
        });

        var task4 = Task.Factory.StartNew(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                foreach (var target in theDriver.Targets.Skip(600).Take(200))
                    await theDriver.Bus.InvokeAsync(target);
            }
        });

        var task5 = Task.Factory.StartNew(async () =>
        {
            for (var i = 0; i < 1000; i++)
            {
                foreach (var target in theDriver.Targets.Skip(800)) await theDriver.Bus.InvokeAsync(target);
            }
        });


        await Task.WhenAll(task1, task2, task3, task4, task5);
    }
}