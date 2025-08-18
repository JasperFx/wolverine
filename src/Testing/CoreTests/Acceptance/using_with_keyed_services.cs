using Lamar.Microsoft.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Attributes;
using Xunit;
using IServiceContainer = JasperFx.IServiceContainer;

namespace CoreTests.Acceptance;

public class using_with_keyed_services : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ThingHandler>();

                opts.Services.AddKeyedSingleton<IThing, RedThing>("Red");
                opts.Services.AddKeyedScoped<IThing, BlueThing>("Blue");
                opts.Services.AddKeyedSingleton<IThing, GreenThing>("Green");

                opts.Services.AddTransient<ThingUser>();
                opts.Services.AddTransient<ThingUserHolder>();
            }).StartAsync();
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }

    [Fact]
    public async Task use_inside_of_deep_dependency_chain()
    {
        var container = _host.Services.GetRequiredService<IServiceContainer>();
        var holder = container.QuickBuild<ThingUserHolder>();
        holder.ThingUser.Thing.ShouldBeOfType<RedThing>();

        await _host.InvokeAsync(new UseThingHolder());
    }

    [Fact]
    public async Task use_as_single_parameter_on_handler()
    {
        await _host.InvokeAsync(new UseThingDirectly());
    }

    [Fact]
    public async Task use_as_singleton_parameter_on_handler()
    {
        await _host.InvokeAsync(new UseSingletonThingDirectly());
    }

    [Fact]
    public async Task use_multiple_parameters_on_handler()
    {
        await _host.InvokeAsync(new UseMultipleThings());
    }
}

public class using_with_keyed_services_and_lamar : IAsyncLifetime
{
    private IHost _host;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseLamar()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType<ThingHandler>();

                opts.Services.AddKeyedSingleton<IThing, RedThing>("Red");
                opts.Services.AddKeyedScoped<IThing, BlueThing>("Blue");
                opts.Services.AddKeyedSingleton<IThing, GreenThing>("Green");

                opts.Services.AddTransient<ThingUser>();
                opts.Services.AddTransient<ThingUserHolder>();
            }).StartAsync();
    }

    public Task DisposeAsync()
    {
        return _host.StopAsync();
    }

    [Fact]
    public async Task use_inside_of_deep_dependency_chain()
    {
        var container = _host.Services.GetRequiredService<IServiceContainer>();
        var holder = container.QuickBuild<ThingUserHolder>();
        holder.ThingUser.Thing.ShouldBeOfType<RedThing>();

        await _host.InvokeAsync(new UseThingHolder());
    }

    [Fact]
    public async Task use_as_single_parameter_on_handler()
    {
        await _host.InvokeAsync(new UseThingDirectly());
    }

    [Fact]
    public async Task use_as_singleton_parameter_on_handler()
    {
        await _host.InvokeAsync(new UseSingletonThingDirectly());
    }

    [Fact]
    public async Task use_multiple_parameters_on_handler()
    {
        await _host.InvokeAsync(new UseMultipleThings());
    }
}

public interface IThing;

public class RedThing : IThing;

public class BlueThing : IThing;

public class GreenThing : IThing;

public record ThingUser([FromKeyedServices("Red")] IThing Thing);

public record ThingUserHolder(ThingUser ThingUser);

public record UseThingDirectly;

public record UseSingletonThingDirectly;

public record UseMultipleThings;

public record UseThingHolder;

[WolverineIgnore]
public class ThingHandler
{
    public static void Handle(UseThingHolder command, ThingUserHolder holder)
    {
        holder.ThingUser.Thing.ShouldBeOfType<RedThing>();
    }

    public static void Handle(
        UseThingDirectly command,
        [FromKeyedServices("Blue")] IThing thing)
    {
        thing.ShouldBeOfType<BlueThing>();
    }

    public static void Handle(UseSingletonThingDirectly command, [FromKeyedServices("Red")] IThing thing)
    {
        thing.ShouldBeOfType<RedThing>();
    }

    public static void Handle(UseMultipleThings command,
        [FromKeyedServices("Green")] IThing green,
        [FromKeyedServices("Red")] IThing red)
    {
        green.ShouldBeOfType<GreenThing>();
        red.ShouldBeOfType<RedThing>();
    }
}