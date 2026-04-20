using GreeterProtoFirstGrpc.Server;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Diagnostics;
using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
///     Smoke tests for <c>codegen-preview --grpc</c>. The command lives in <c>Wolverine</c> core,
///     but the integration point — a discovered <see cref="GrpcGraph"/> reachable through the
///     <c>ICodeFileCollection</c> DI seam — only exists when <c>Wolverine.Grpc</c> is referenced,
///     so end-to-end coverage lives here rather than in <c>CoreTests</c>.
/// </summary>
public class codegen_preview_grpc_tests
{
    [Fact]
    public async Task codegen_preview_generates_code_for_grpc_service()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    // Pull in the GreeterProtoFirstGrpc server assembly — it has the abstract
                    // [WolverineGrpcService] stub plus the handler that satisfies every unary RPC.
                    opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
                })
                .ConfigureServices(services =>
                {
                    services.AddWolverineGrpc();
                })
                .StartAsync();

            // Mimic MapProtoFirstServices() without needing a WebApplication: discover the
            // stubs, then push the graph into the supplemental code-file collection so that
            // services.GetServices<ICodeFileCollection>() reaches it — exactly the seam
            // PreviewGrpcCode walks.
            var graph = host.Services.GetRequiredService<GrpcGraph>();
            graph.DiscoverServices();
            graph.Chains.ShouldNotBeEmpty("the Greeter proto-first stub should be discovered");

            var supplemental = host.Services.GetRequiredService<WolverineSupplementalCodeFiles>();
            if (!supplemental.Collections.Contains(graph))
            {
                supplemental.Collections.Add(graph);
            }

            // Now replay what PreviewGrpcCode does: normalize a user input into the expected
            // file name, walk all ICodeFileCollection instances, generate code for the match.
            var expectedFileName = WolverineDiagnosticsCommand.GrpcInputToFileName("Greeter");
            expectedFileName.ShouldBe("GreeterGrpcHandler");

            var serviceVariableSource = host.Services.GetService<IServiceVariableSource>();
            var allCollections = host.Services.GetServices<ICodeFileCollection>().ToArray();

            ICodeFileCollection? foundCollection = null;
            ICodeFile? foundFile = null;

            foreach (var collection in allCollections)
            {
                foreach (var file in collection.BuildFiles())
                {
                    if (string.Equals(file.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase))
                    {
                        foundCollection = collection;
                        foundFile = file;
                        break;
                    }
                }

                if (foundFile != null) break;
            }

            foundFile.ShouldNotBeNull($"a gRPC chain with file name '{expectedFileName}' should be discoverable via ICodeFileCollection");
            foundCollection.ShouldNotBeNull();

            var generatedAssembly = foundCollection.StartAssembly(foundCollection.Rules);
            foundFile.AssembleTypes(generatedAssembly);
            var code = generatedAssembly.GenerateCode(serviceVariableSource);

            code.ShouldNotBeNullOrEmpty();
            // The generated wrapper subclasses the abstract stub and overrides the unary methods.
            code.ShouldContain("GreeterGrpcHandler");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    [Fact]
    public async Task codegen_preview_reports_no_match_for_unknown_grpc_input()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
                })
                .ConfigureServices(services =>
                {
                    services.AddWolverineGrpc();
                })
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            graph.DiscoverServices();

            var supplemental = host.Services.GetRequiredService<WolverineSupplementalCodeFiles>();
            if (!supplemental.Collections.Contains(graph))
            {
                supplemental.Collections.Add(graph);
            }

            var expectedFileName = WolverineDiagnosticsCommand.GrpcInputToFileName("DoesNotExist");
            var allCollections = host.Services.GetServices<ICodeFileCollection>().ToArray();

            var match = allCollections
                .SelectMany(c => c.BuildFiles())
                .FirstOrDefault(f =>
                    string.Equals(f.FileName, expectedFileName, StringComparison.OrdinalIgnoreCase));

            match.ShouldBeNull("no chain should match an unknown proto service name");
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }
}
