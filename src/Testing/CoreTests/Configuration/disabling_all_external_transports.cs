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
        #region sample_disabling_external_transports

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // do whatever you need to configure Wolverine
            })
            
            // Override the Wolverine configuration to disable all
            // external transports, broker connectivity, and incoming/outgoing
            // messages to run completely locally
            .ConfigureServices(services => services.DisableAllExternalWolverineTransports())
            
            .StartAsync();

        #endregion

        var options = host.Services.GetRequiredService<WolverineOptions>();
        
        options.Advanced.StubAllExternalTransports.ShouldBeTrue();
    }
}