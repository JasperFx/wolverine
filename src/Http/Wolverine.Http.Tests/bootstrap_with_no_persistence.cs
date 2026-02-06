using Alba;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests;

public class bootstrap_with_no_persistence
{
    [Fact]
    public async Task start_up_with_no_persistence()
    {
        #region sample_bootstrap_with_no_persistence

        using var host = await AlbaHost.For<WolverineWebApi.Program>(builder =>
        {
            builder.ConfigureServices(services =>
            {
                // You probably have to do both
                services.DisableAllExternalWolverineTransports();
                services.DisableAllWolverineMessagePersistence();
            });
        });

        #endregion
        
        host.GetRuntime().Storage.ShouldBeOfType<NullMessageStore>();
    }
}