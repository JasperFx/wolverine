using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Configuration;

public class using_solo_mode_as_override
{
    [Fact]
    public async Task use_the_solo_mode_override()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .ConfigureServices(services => services.UseWolverineSoloMode())
            .StartAsync();
        
        var options = host.Services.GetRequiredService<WolverineOptions>();
        
        options.Durability.Mode.ShouldBe(DurabilityMode.Solo);
    }
}