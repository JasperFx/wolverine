using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.ComplianceTests;
using Wolverine.Runtime;
using Xunit;

namespace CoreTests.Configuration;

public class extension_loading_and_discovery : IntegrationContext
{
    public extension_loading_and_discovery(DefaultApp @default) : base(@default)
    {
    }

    [Fact]
    public void the_application_still_wins()
    {
        using var host = WolverineHost.For(options =>
        {
            options.DisableConventionalDiscovery();
            options.Include<OptionalExtension>();
            options.Services.AddScoped<IColorService, BlueService>();
        });

        using var scope = host.Services.CreateScope();
        
        scope.ServiceProvider.GetRequiredService<IColorService>()
            .ShouldBeOfType<BlueService>();
    }

    [Fact]
    public void try_find_extension_miss()
    {
        using var host = WolverineHost.For(options =>
        {
            options.DisableConventionalDiscovery();
        });
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.TryFindExtension<OptionalExtension>().ShouldBeNull();
    }

    [Fact]
    public void try_find_extension_hit()
    {
        using var host = WolverineHost.For(options =>
        {
            options.DisableConventionalDiscovery();
            options.Include<OptionalExtension>();
        });
        
        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.TryFindExtension<OptionalExtension>().ShouldBeOfType<OptionalExtension>().ShouldNotBeNull();
    }

    [Fact]
    public void try_find_extension_hit_2()
    {
        var options = new WolverineOptions();
        options.DisableConventionalDiscovery();
        options.Services.AddSingleton<IWolverineExtension>(new OptionalExtension());

        using var host = new HostBuilder()
            .UseWolverine(opts => { opts.DisableConventionalDiscovery(); })
            .ConfigureServices(services => services.AddSingleton<IWolverineExtension, OptionalExtension>())
            .Start();

        var runtime = host.Services.GetRequiredService<IWolverineRuntime>();
        runtime.TryFindExtension<OptionalExtension>().ShouldBeOfType<OptionalExtension>().ShouldNotBeNull();
    }

    [Fact]
    public void will_apply_an_extension()
    {
        using var runtime = WolverineHost.For(opts =>
        {
            #region sample_explicitly_add_extension
            opts.Include<OptionalExtension>();
            #endregion

            opts.DisableConventionalDiscovery();
        });

        using var scope = runtime.Services.CreateScope();
        
        scope.ServiceProvider.GetRequiredService<IColorService>()
            .ShouldBeOfType<RedService>();
    }

    [Fact]
    public void will_only_apply_extension_once()
    {
        using var host = WolverineHost.For(registry =>
        {
            registry.Include<OptionalExtension>();
            registry.Include<OptionalExtension>();
            registry.Include<OptionalExtension>();
            registry.Include<OptionalExtension>();
        });
        
        host.Get<IServiceContainer>().RegistrationsFor<IColorService>()
            .Count().ShouldBe(1);
    }

    [Fact]
    public void picks_up_on_handlers_from_extension()
    {
        with(x => x.Include<MyExtension>());

        var handlerChain = chainFor<ExtensionMessage>();
        handlerChain.Handlers.Single()
            .HandlerType.ShouldBe(typeof(ExtensionThing));
    }
}

public interface IColorService;

public class RedService : IColorService;

public class BlueService : IColorService;

public class OptionalExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.Services.AddScoped<IColorService, RedService>();
    }
}

public class MyExtension : IWolverineExtension
{
    public void Configure(WolverineOptions options)
    {
        options.IncludeType<ExtensionThing>();
    }
}

public class ExtensionMessage;

public class ExtensionThing
{
    public void Handle(ExtensionMessage message)
    {
    }
}