using JasperFx.Events.InMemory;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.InMemory;

namespace InMemoryTests;

internal static class TestHosts
{
    // Development, explicitly: the generic host defaults to Production, which the prototyping store refuses
    public static Task<IHost> StartAsync(Action<WolverineOptions> configure,
        Action<InMemoryStoreOptions>? store = null)
        => Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Development)
            .UseWolverine(opts =>
            {
                opts.UseInMemoryStoreForPrototyping(store);
                configure(opts);
            })
            .StartAsync();
}
