using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using WolverineWebApi;
using Xunit;

namespace Wolverine.Http.Tests;

// GH-2925: the generated HttpEndpointRegistry manifest captures the discovered endpoint types at
// `codegen write` time so that, under TypeLoadMode.Static, HTTP endpoint discovery can skip the
// HttpChainSource.FindActions ExportedTypes scan (the Wolverine.Http counterpart to GH-2906).
public class http_endpoint_manifest
{
    [Fact]
    public void generated_endpoint_registry_captures_endpoint_types()
    {
        // Same minimal codegen harness as smoke_test_code_generation_of_endpoints_with_no_service_dependencies.
        // AssemblyGenerator is qualified inline to avoid importing JasperFx.RuntimeCompiler (whose obsolete
        // InitializeSynchronously extension would collide with the JasperFx.CodeGeneration one used below).
        var registry = new ServiceCollection();
        registry.AddSingleton<WolverineHttpOptions>();
        registry.AddTransient<IServiceVariableSource>(c =>
            new ServiceCollectionServerVariableSource((ServiceContainer)c.GetRequiredService<IServiceContainer>()));
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IAssemblyGenerator, JasperFx.RuntimeCompiler.AssemblyGenerator>();

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();
        var parent = new HttpGraph(new WolverineOptions { ApplicationAssembly = GetType().Assembly }, container);

        var codeFile = new HttpEndpointRegistryCodeFile([typeof(FakeEndpoint)]);

        // Generate + compile + attach just the registry code file.
        codeFile.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container.Services);

        codeFile.RegistryType.ShouldNotBeNull();

        var instance = (HttpEndpointRegistry)Activator.CreateInstance(codeFile.RegistryType!)!;
        instance.EndpointTypes().ShouldContain(typeof(FakeEndpoint));
    }
}
