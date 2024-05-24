using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests;

public class playing
{
    [Fact]
    public async Task give_it_a_shot()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<IServiceProviderFactory<IServiceCollection>, Catcher>();
            })
            .StartAsync();

        var provider = host.Services.GetRequiredService<IServiceProvider>();
        // var catcher = host.Services.GetRequiredService<IServiceProviderFactory<IServiceCollection>>().ShouldBeOfType<Catcher>();
        // catcher.Services.Any().ShouldBeTrue();
        Debug.WriteLine(provider);
    }
}

public class Catcher : IServiceProviderFactory<IServiceCollection>
{
    public IServiceCollection CreateBuilder(IServiceCollection services)
    {
        Services = services;
        return services;
    }

    public IServiceProvider CreateServiceProvider(IServiceCollection containerBuilder)
    {
        Services = containerBuilder;
        return containerBuilder.BuildServiceProvider();
    }

    public IServiceCollection Services { get; set; }
}