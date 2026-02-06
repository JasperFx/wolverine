using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

public class disable_external_listeners
{
    [Fact]
    public async Task listeners_are_not_active()
    {
        using var host = await Host.CreateDefaultBuilder()

            #region sample_disable_all_listeners

            .UseWolverine(opts =>
            {
                // This will disable all message listening to
                // external message brokers
                opts.DisableAllExternalListeners = true;
                
                opts.DisableConventionalDiscovery();

                // This could never, ever work
                opts.UseRabbitMq().AutoProvision();
                opts.ListenToRabbitQueue("incoming");
            }).StartAsync();

        #endregion

        var activeListeners = host.GetRuntime().Endpoints.ActiveListeners().ToArray();
        activeListeners
            .Any().ShouldBeFalse();
    }
}