using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Configuration;

public class disabling_all_external_transports
{
    [Fact]
    public async Task disable_all_external_transports_from_extension_method()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .ConfigureServices(services => services.DisableAllExternalWolverineTransports())
            .StartAsync();

        var options = host.Services.GetRequiredService<WolverineOptions>();
        
        options.Advanced.StubAllExternalTransports.ShouldBeTrue();
    }
}