using CoreTests.Persistence.Sagas;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace CoreTests.Acceptance;

public class warn_if_using_wolverine_before_app_is_started 
{

    [Fact]
    public void will_throw_on_operations()
    {
        using var theHost = Host.CreateDefaultBuilder().UseWolverine().Build();
        Should.Throw<WolverineHasNotStartedException>(() => theHost.Services.GetRequiredService<IMessageBus>());
    }
}