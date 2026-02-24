using Alba;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using System.Reflection;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_using_host_stop
{
    [Fact]
    public async Task stops_wolverine_runtime_when_created_via_host_builder()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Services.DisableAllWolverineMessagePersistence();
        builder.Services.DisableAllExternalWolverineTransports();
        builder.Services.AddWolverine(_ => { });
        var host = await AlbaHost.For(builder, _ => { });
        var runtime = host.GetRuntime();
        var checkPoints = new bool[3];

        checkPoints[0] = IsRunning(runtime);
        await host.StopAsync();
        checkPoints[1] = IsRunning(runtime);
        await host.DisposeAsync();
        checkPoints[2] = IsRunning(runtime);

        // Note WolverineRuntime is stopped when host.StopAsync() is called,
        // which is expected as WolverineRuntime is IHostedService.
        checkPoints.ShouldBe([true, false, false]);
    }

    [Fact]
    public async Task does_not_stop_wolverine_runtime_when_created_via_web_factory()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Services.DisableAllWolverineMessagePersistence();
        builder.Services.DisableAllExternalWolverineTransports();
        builder.Services.AddWolverine(_ => { });
        var host = await AlbaHost.For<WolverineWebApi.Program>(_ => { });
        var wolverineRuntime = host.GetRuntime();
        var checkPoints = new bool[3];

        checkPoints[0] = IsRunning(wolverineRuntime);
        await host.StopAsync();
        checkPoints[1] = IsRunning(wolverineRuntime);
        await host.DisposeAsync();
        checkPoints[2] = IsRunning(wolverineRuntime);

        // If you expect host.StopAsync() to stop WolverineRuntime - 
        // [true, false, false] - it's not the case here.
        checkPoints.ShouldBe([true, true, false]);
    }

    [Fact]
    public async Task wolverine_runtime_can_be_stopped_explicitly_when_created_via_web_factory()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Services.DisableAllWolverineMessagePersistence();
        builder.Services.DisableAllExternalWolverineTransports();
        builder.Services.AddWolverine(_ => { });
        var host = await AlbaHost.For<WolverineWebApi.Program>(_ => { });
        var wolverineRuntime = host.GetRuntime();
        var checkPoints = new bool[3];

        checkPoints[0] = IsRunning(wolverineRuntime);
        await host.GetRuntime().StopAsync(default); // Can be stopped explicitly.
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
}
