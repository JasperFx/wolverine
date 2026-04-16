using Alba;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Shouldly;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_using_host_stop
{
    public enum HostType
    {
        WebApplicationBuilder,
        AlbaHostWithWebApplicationBuilder,
        AlbaHostWithFactory
    }

    private class HostTypeData : TheoryData<HostType>
    {
        public HostTypeData() => AddRange(Enum.GetValues<HostType>());
    }

    [Theory]
    [ClassData(typeof(HostTypeData))]
    public async Task wolverine_runtime_stops_when_host_is_stopped(HostType type)
    {
        using var host = await CreateHostAsync(type);
        var wolverineRuntime = host.GetRuntime();
        var checkPoints = new bool[2];

        checkPoints[0] = IsRunning(wolverineRuntime);
        await host.StopAsync();
        checkPoints[1] = IsRunning(wolverineRuntime);

        checkPoints.ShouldBe([true, false]);
    }

    [Theory]
    [ClassData(typeof(HostTypeData))]
    public async Task wolverine_runtime_stops_when_host_is_disposed(HostType type)
    {
        using var host = await CreateHostAsync(type);
        var wolverineRuntime = host.GetRuntime();
        var checkPoints = new bool[2];

        checkPoints[0] = IsRunning(wolverineRuntime);
        await host.As<IAsyncDisposable>().DisposeAsync();
        checkPoints[1] = IsRunning(wolverineRuntime);

        checkPoints.ShouldBe([true, false]);
    }

    static bool IsRunning(WolverineRuntime runtime)
    {
        var field = typeof(WolverineRuntime).GetField("_hasStopped",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return (bool?)field?.GetValue(runtime) == false;
    }

    private static async Task<IHost> CreateHostAsync(HostType hostType) =>
        hostType switch
        {
            HostType.WebApplicationBuilder =>
                await CreateHostWithWebApplicationBuilder(),

            HostType.AlbaHostWithWebApplicationBuilder =>
                await AlbaHost.For(CreateWebApplicationBuilder(), _ => { }),

            _ =>
                await CreateAlbaHostWithWithFactory()
        };

    private static async Task<IHost> CreateAlbaHostWithWithFactory()
    {
        return await AlbaHost.For<WolverineWebApi.Program>(x =>
            x.ConfigureServices(ConfigureWolverine));
    }

    private static async Task<IHost> CreateHostWithWebApplicationBuilder()
    {
        var builder = CreateWebApplicationBuilder();
        var host = builder.Build();
        await host.StartAsync();
        return host;
    }

    private static WebApplicationBuilder CreateWebApplicationBuilder()
    {
        var builder = WebApplication.CreateBuilder([]);
        ConfigureWolverine(builder.Services);
        builder.Services.AddWolverine(_ => { });
        return builder;
    }

    private static void ConfigureWolverine(IServiceCollection services)
    {
        services
            .RunWolverineInSoloMode()
            .DisableAllWolverineMessagePersistence()
            .DisableAllExternalWolverineTransports();
    }
}