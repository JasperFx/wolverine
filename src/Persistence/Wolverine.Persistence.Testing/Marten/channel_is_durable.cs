using IntegrationTests;
using Marten;
using Shouldly;
using TestingSupport;
using Wolverine.Persistence.Marten;
using Wolverine.Runtime;
using Wolverine.Util;
using Xunit;

namespace Wolverine.Persistence.Testing.Marten;

public class channel_is_durable : PostgresqlContext
{
    [Fact]
    public void channels_that_are_or_are_not_durable()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.Services.AddMarten(Servers.PostgresConnectionString)
                .IntegrateWithWolverine();
        });

        var runtime = host.Get<IWolverineRuntime>();
        runtime.GetOrBuildSendingAgent("local://one".ToUri()).IsDurable.ShouldBeFalse();
        runtime.GetOrBuildSendingAgent("local://durable/two".ToUri()).IsDurable.ShouldBeTrue();

        runtime.GetOrBuildSendingAgent("tcp://server1:2000".ToUri()).IsDurable.ShouldBeFalse();
        runtime.GetOrBuildSendingAgent("tcp://server2:3000/durable".ToUri()).IsDurable.ShouldBeTrue();
    }
}
