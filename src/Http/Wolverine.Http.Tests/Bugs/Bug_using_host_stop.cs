using Alba;
using JasperFx.Core.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Shouldly;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime;
using Wolverine.Tracking;
using System;

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
        await host.StopAsync(TestContext.Current.CancellationToken);
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
        var field = typeof(WolverineRuntime).GetField("_stopped",
            BindingFlags.NonPublic | BindingFlags.Instance);
        field.ShouldNotBeNull("WolverineRuntime._stopped was renamed; this probe needs updating");
        return field.GetValue(runtime) is null;
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
        // Some macOS systems have the AirPlay Receiver bound to port 5000 by default.
        // Force the ASP.NET Core host to bind to an ephemeral port (0) to avoid collisions.
        var previous = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", "http://127.0.0.1:0");
        try
        {
            return await AlbaHost.For<WolverineWebApi.Program>(x =>
                x.ConfigureServices(ConfigureWolverine));
        }
        finally
        {
            // Restore previous value to avoid surprising other tests
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", previous);
        }
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

        // Bind to an ephemeral port to avoid binding to the platform default (5000)
        // which on macOS can be occupied by the AirPlay Receiver.
        builder.WebHost.UseUrls("http://127.0.0.1:0");

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