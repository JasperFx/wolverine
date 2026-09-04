using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Runtime.Handlers;
using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
/// GH-4156. gRPC service chains are a third <c>ICodeFileCollection</c> alongside handler chains (GH-4151)
/// and HTTP endpoint chains, with the same exposure and -- unlike HTTP -- the same lazy failure the issue
/// described. <c>HttpChain.BuildEndpoint</c> already forces the handler build for every chain in
/// <see cref="TypeLoadMode.Static" />, so HTTP at least failed at mapping (with an unreadable exception).
/// <c>GrpcGraph.DiscoverServices</c> forces nothing, so a missing pre-built type here really did wait for
/// the first RPC to that service, with the host reporting healthy the whole time.
/// </summary>
public class Bug_4156_static_mode_service_types
{
    [Fact]
    public void static_mode_without_pre_built_service_types_fails_discovery()
    {
        var graph = buildGraph(TypeLoadMode.Static);

        var ex = Should.Throw<MissingPreBuiltTypesException>(() => graph.DiscoverServices(new WolverineGrpcOptions()));

        // Name the assembly that was searched, because "the types are missing" and "the types are in the
        // other assembly" call for different fixes.
        ex.Message.ShouldContain(typeof(Bug_4156_static_mode_service_types).Assembly.GetName().Name!);
    }

    [Fact]
    public void auto_mode_discovers_the_same_services_without_complaint()
    {
        // False-positive guard: the assertion must fire on a genuinely missing pre-built type and on
        // nothing else. Auto generates what it cannot load, so the very same graph is fine.
        var graph = buildGraph(TypeLoadMode.Auto);

        Should.NotThrow(() => graph.DiscoverServices(new WolverineGrpcOptions()));
    }

    // Same minimal codegen harness as grpc_direct_mapped_manifest.
    private static GrpcGraph buildGraph(TypeLoadMode mode)
    {
        var registry = new ServiceCollection();
        registry.AddLogging();
        registry.AddTransient<IServiceVariableSource>(c =>
            new ServiceCollectionServerVariableSource((ServiceContainer)c.GetRequiredService<IServiceContainer>()));
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IAssemblyGenerator, JasperFx.RuntimeCompiler.AssemblyGenerator>();

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();

        var options = new WolverineOptions { ApplicationAssembly = typeof(Bug_4156_static_mode_service_types).Assembly };
        options.CodeGeneration.TypeLoadMode = mode;

        return new GrpcGraph(options, container);
    }
}
