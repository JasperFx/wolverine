using EfCoreTests.MultiTenancy;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using SharedPersistenceModels.Items;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.EntityFrameworkCore;

namespace EfCoreTests;

[Collection("sqlserver")]
public class EfCoreCompilationScenarios
{
    [Fact]
    public async Task ef_context_is_scoped_and_options_are_scoped()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CreateItemHandler));

            // Default of both is scoped
            opts.Services.AddDbContext<SampleDbContext>();
            
            opts.UseEntityFrameworkCoreTransactions();
        });

        await host.MessageBus().InvokeAsync(new CreateItem { Name = "foo" });
    }

    [Fact]
    public async Task ef_context_is_scoped_and_options_are_singleton()
    {
        var host = await WolverineHost.ForAsync(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CreateItemHandler));
            // Default of both is scoped
            opts.Services.AddDbContext<SampleDbContext>(optionsLifetime: ServiceLifetime.Singleton);
            
            opts.UseEntityFrameworkCoreTransactions();
        });

        await host.MessageBus().InvokeAsync(new CreateItem { Name = "foo" });
        await host.StopAsync();
        host.Dispose();
    }

    [Fact]
    public async Task ef_context_is_singleton_and_options_are_singleton()
    {
        using var host = WolverineHost.For(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CreateItemHandler));
            
            // Default of both is scoped
            opts.Services.AddDbContext<SampleDbContext>(ServiceLifetime.Singleton, ServiceLifetime.Singleton);
            
            opts.UseEntityFrameworkCoreTransactions();
        });

        await host.MessageBus().InvokeAsync(new CreateItem { Name = "foo" });
    }
}

public class CreateItem
{
    public string Name { get; set; }
}

public class CreateItemHandler
{
    public Task Handle(CreateItem command, SampleDbContext context)
    {
        return Task.CompletedTask;
    }
}

