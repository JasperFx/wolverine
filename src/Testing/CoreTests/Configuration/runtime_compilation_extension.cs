using JasperFx.CodeGeneration;
using JasperFx.RuntimeCompiler;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Xunit;

namespace CoreTests.Configuration;

public class runtime_compilation_extension
{
    [Fact]
    public async Task use_runtime_compilation_registers_assembly_generator()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRuntimeCompilation();
            }).StartAsync();

        var generator = host.Services.GetService<IAssemblyGenerator>();
        generator.ShouldNotBeNull();
        generator.ShouldBeOfType<AssemblyGenerator>();
    }

    [Fact]
    public async Task use_runtime_compilation_is_idempotent()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRuntimeCompilation();
                opts.UseRuntimeCompilation();
                opts.UseRuntimeCompilation();
            }).StartAsync();

        // Should resolve a single registered instance and not throw at startup
        var generator = host.Services.GetService<IAssemblyGenerator>();
        generator.ShouldNotBeNull();
    }

    [Fact]
    public void add_wolverine_runtime_compilation_registers_assembly_generator_directly_on_services()
    {
        var services = new ServiceCollection();

        services.AddWolverineRuntimeCompilation();

        var provider = services.BuildServiceProvider();
        var generator = provider.GetService<IAssemblyGenerator>();

        generator.ShouldNotBeNull();
        generator.ShouldBeOfType<AssemblyGenerator>();
    }

    [Fact]
    public void add_wolverine_runtime_compilation_does_not_replace_an_existing_registration()
    {
        var services = new ServiceCollection();
        var existing = new AssemblyGenerator { AssemblyName = "Custom" };
        services.AddSingleton<IAssemblyGenerator>(existing);

        services.AddWolverineRuntimeCompilation();

        var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IAssemblyGenerator>().ShouldBeSameAs(existing);
    }
}
