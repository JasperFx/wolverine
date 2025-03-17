using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Wolverine.AmazonSns.Tests;

public class bootstrapping
{
    [Fact]
    public async Task create_an_open_client()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.UseAmazonSnsTransportLocally(); }).StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSnsTransport();

        // Just a smoke test on configuration here
        var topicNames = await transport.Client.ListTopicsAsync();
    }
}
