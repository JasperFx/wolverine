using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute.Extensions;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Configuration;

public class using_configure_wolverine
{
    [Fact]
    public async Task use_configure_wolverine()
    {
        #region sample_using_configure_wolverine

        var builder = Host.CreateApplicationBuilder();
        
        // Baseline Wolverine configuration
        builder.Services.AddWolverine(opts =>
        {
            
        });
        
        // This would be applied as an extension
        builder.Services.ConfigureWolverine(w =>
        {
            // There is a specific helper for this, but just go for it
            // as an easy example
            w.Durability.Mode = DurabilityMode.Solo;
        });

        using var host = builder.Build();
        
        host.Services.GetRequiredService<IWolverineRuntime>()
            .Options
            .Durability
            .Mode
            .ShouldBe(DurabilityMode.Solo);

        #endregion
    }
}