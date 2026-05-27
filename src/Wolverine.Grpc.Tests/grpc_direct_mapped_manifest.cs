using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.CodeGeneration.Services;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-2907: the fourth gRPC discovery mode — direct-mapped hand-written services (mapped without a
// generated wrapper by MapWolverineGrpcServices) — is now captured in the GrpcServiceRegistry manifest.
// This compiles the registry code file and verifies the new DirectMappedServiceTypes() accessor
// round-trips the captured types (emit -> compile -> read).
public class grpc_direct_mapped_manifest
{
    [Fact]
    public void registry_round_trips_direct_mapped_service_types()
    {
        // Same minimal codegen harness as the HTTP manifest test; AssemblyGenerator is qualified inline
        // to avoid importing JasperFx.RuntimeCompiler (whose obsolete InitializeSynchronously extension
        // would collide with the JasperFx.CodeGeneration one used below).
        var registry = new ServiceCollection();
        registry.AddTransient<IServiceVariableSource>(c =>
            new ServiceCollectionServerVariableSource((ServiceContainer)c.GetRequiredService<IServiceContainer>()));
        registry.AddSingleton<IServiceCollection>(registry);
        registry.AddSingleton<IServiceContainer, ServiceContainer>();
        registry.AddSingleton<IAssemblyGenerator, JasperFx.RuntimeCompiler.AssemblyGenerator>();

        var container = registry.BuildServiceProvider().GetRequiredService<IServiceContainer>();
        var parent = new GrpcGraph(new WolverineOptions { ApplicationAssembly = GetType().Assembly }, container);

        // Only the direct-mapped slot is populated.
        var codeFile = new GrpcServiceRegistryCodeFile([], [], [], [typeof(SampleManifestService)]);

        codeFile.As<ICodeFile>().InitializeSynchronously(parent.Rules, parent, parent.Container.Services);

        codeFile.RegistryType.ShouldNotBeNull();

        var instance = (GrpcServiceRegistry)Activator.CreateInstance(codeFile.RegistryType!)!;

        instance.DirectMappedServiceTypes().ShouldBe([typeof(SampleManifestService)]);
        instance.ProtoFirstStubTypes().ShouldBeEmpty();
        instance.CodeFirstContractTypes().ShouldBeEmpty();
        instance.HandWrittenServiceTypes().ShouldBeEmpty();
    }
}

// Plain type used only as a manifest payload — deliberately NOT named *GrpcService and not attributed,
// so it is never picked up by gRPC service discovery in other tests.
public class SampleManifestService;
