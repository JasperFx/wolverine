using System.Diagnostics;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Runtime;

public class CommandBusTests
{
    [Fact]
    public void use_current_activity_root_id_as_correlation_id_if_exists()
    {
        var activity = new Activity("process");
        activity?.Start();

        try
        {
            var bus = new CommandBus(new MockWolverineRuntime());
            bus.CorrelationId.ShouldBe(activity.RootId);
        }
        finally
        {
            activity?.Stop();
        }
    }
}