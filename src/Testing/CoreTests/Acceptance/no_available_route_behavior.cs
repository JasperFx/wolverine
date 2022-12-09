using System.Threading.Tasks;
using Wolverine.Runtime.Routing;
using Xunit;

namespace CoreTests.Acceptance;

public class no_available_route_behavior : IntegrationContext
{
    public no_available_route_behavior(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public async Task throw_no_route_exception_by_default()
    {
        await Should.ThrowAsync<IndeterminateRoutesException>(async () =>
        {
            await Publisher.SendAsync(new MessageWithNoRoutes());
        });
    }
}

public class MessageWithNoRoutes
{
}