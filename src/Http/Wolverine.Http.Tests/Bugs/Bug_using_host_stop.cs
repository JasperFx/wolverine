using Alba;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using System.Reflection;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_using_host_stop
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task stops_wolverine_runtime(bool useWebApplicationBuilder)
    {
        await using var host = await CreateHostAsync(useWebApplicationBuilder);
        var wolverineRuntime = host.GetRuntime();
        var checkPoints = new bool[3];

        checkPoints[0] = IsRunning(wolverineRuntime);
        await host.StopAsync();
        checkPoints[1] = IsRunning(wolverineRuntime);
        await host.DisposeAsync();
        checkPoints[2] = IsRunning(wolverineRuntime);

        // Note WolverineRuntime is stopped when host.StopAsync() is called,
        // which is expected as WolverineRuntime is IHostedService.
        checkPoints.ShouldBe([true, false, false]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task wolverine_runtime_can_be_stopped_explicitly(bool useWebApplicationBuilder)
    {
        await using var host = await CreateHostAsync(useWebApplicationBuilder);
        var wolverineRuntime = host.GetRuntime();
        var checkPoints = new bool[3];

        checkPoints[0] = IsRunning(wolverineRuntime);
        await wolverineRuntime.StopAsync(default); // can be stopped explicitly
        await host.StopAsync();
        checkPoints[1] = IsRunning(wolverineRuntime);
        await host.DisposeAsync();
        checkPoints[2] = IsRunning(wolverineRuntime);

        checkPoints.ShouldBe([true, false, false]);
    }

    static bool IsRunning(WolverineRuntime runtime)
    {
        var field = typeof(WolverineRuntime).GetField("_hasStopped", BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool?)field?.GetValue(runtime) == false;
    }

    static Task<IAlbaHost> CreateHostAsync(bool useWebApplicationBuilder)
    {
        if (useWebApplicationBuilder)
        {
            var builder = WebApplication.CreateBuilder([]);
            builder.Services.DisableAllWolverineMessagePersistence();
            builder.Services.DisableAllExternalWolverineTransports();
            builder.Services.AddWolverine(_ => { });

            return AlbaHost.For(builder, _ => { });
        }

        return AlbaHost.For<WolverineWebApi.Program>(_ => { });
    }
}
