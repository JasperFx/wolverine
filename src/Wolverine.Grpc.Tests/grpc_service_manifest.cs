using GreeterProtoFirstGrpc.Server;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-2926: the generated GrpcServiceRegistry manifest captures the discovered service types (across
// proto-first, code-first, and hand-written discovery) at `codegen write` time so that, under
// TypeLoadMode.Static, GrpcGraph.DiscoverServices can skip the GetExportedTypes scans (the
// Wolverine.Grpc counterpart to GH-2906).
[Collection(GrpcSerialTestsCollection.Name)]
public class grpc_service_manifest
{
    [Fact]
    public async Task generated_registry_captures_discovered_service_types()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // The GreeterProtoFirstGrpc server assembly carries the abstract
                    // [WolverineGrpcService] proto-first stub.
                    opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
                })
                .ConfigureServices(services => services.AddWolverineGrpc())
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var grpcOptions = host.Services.GetRequiredService<WolverineGrpcOptions>();
            graph.DiscoverServices(grpcOptions);
            graph.Chains.ShouldNotBeEmpty("the Greeter proto-first stub should be discovered");

            var registryFile = graph.BuildFiles()
                .FirstOrDefault(f => f.FileName == GrpcServiceRegistry.GeneratedTypeName);
            registryFile.ShouldNotBeNull("BuildFiles should include the generated gRPC service registry");

            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var generatedAssembly = ((ICodeFileCollection)graph).StartAssembly(graph.Rules);
            registryFile.AssembleTypes(generatedAssembly);
            var code = generatedAssembly.GenerateCode(serviceVariableSource);

            // All four discovery flavors get their own accessor (GH-2907 adds direct-mapped)...
            code.ShouldContain("ProtoFirstStubTypes()");
            code.ShouldContain("CodeFirstContractTypes()");
            code.ShouldContain("HandWrittenServiceTypes()");
            code.ShouldContain("DirectMappedServiceTypes()");

            // ...and the discovered proto-first stub type is captured.
            code.ShouldContain(nameof(GreeterGrpcService));

            // Satellite parity (GH-2907): every direct-mapped service the live map-time scan would find
            // across the registered assemblies — including the referenced GreeterProtoFirstGrpc.Server
            // satellite — is captured in the manifest, so MapWolverineGrpcServices can skip its scan
            // under TypeLoadMode.Static.
            var assemblies = ((WolverineRuntime)host.Services.GetRequiredService<IWolverineRuntime>())
                .Options.Assemblies;
            foreach (var type in WolverineGrpcExtensions.FindGrpcServiceTypes(assemblies, graph))
            {
                code.ShouldContain($"typeof({type.FullNameInCode()})");
            }
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}
