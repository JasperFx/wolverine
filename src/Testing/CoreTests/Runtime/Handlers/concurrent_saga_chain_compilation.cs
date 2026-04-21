using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace CoreTests.Runtime.Handlers;

public class concurrent_saga_chain_compilation
{
    [Fact]
    public async Task concurrent_first_time_resolution_of_a_saga_handler_does_not_throw()
    {
        const int iterations = 20;
        const int concurrency = 64;

        for (var i = 0; i < iterations; i++)
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.Discovery.DisableConventionalDiscovery().IncludeType<RaceSaga>();
                    opts.MultipleHandlerBehavior = MultipleHandlerBehavior.Separated;
                    opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Dynamic;
                }).StartAsync();

            var graph = host.Services.GetRequiredService<HandlerGraph>();

            var start = new ManualResetEventSlim(false);
            var failures = new ConcurrentBag<Exception>();
            var threads = new Thread[concurrency];

            for (var t = 0; t < concurrency; t++)
            {
                threads[t] = new Thread(() =>
                {
                    start.Wait();
                    try
                    {
                        graph.HandlerFor(typeof(StartRace));
                    }
                    catch (Exception ex)
                    {
                        failures.Add(ex);
                    }
                });
                threads[t].Start();
            }

            start.Set();

            foreach (var thread in threads) thread.Join();

            failures.ShouldBeEmpty();
        }
    }
}

public record StartRace(Guid Id);

public class RaceSaga : Wolverine.Saga
{
    public Guid Id { get; set; }

    public static RaceSaga Start(StartRace cmd) => new() { Id = cmd.Id };
}
