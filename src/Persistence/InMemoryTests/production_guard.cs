using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.InMemory;

namespace InMemoryTests;

public class production_guard
{
    [Fact]
    public async Task the_store_still_refuses_to_start_in_production()
    {
        // Through Wolverine as much as through the bare registration: the guard is a hosted service
        using var host = Host.CreateDefaultBuilder()
            .UseEnvironment(Environments.Production)
            .UseWolverine(opts => opts.UseInMemoryStoreForPrototyping())
            .Build();

        await Should.ThrowAsync<NotSupportedException>(() => host.StartAsync(TestContext.Current.CancellationToken));
    }
}
