using GreeterProtoFirstGrpc.Server;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.CodeGeneration.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Wolverine.Grpc.Tests.GrpcMiddlewareScoping;
using Xunit;
namespace Wolverine.Grpc.Tests;

/// <summary>
///     GH-3692: <c>dotnet run -- codegen write</c> wrote an empty file (a bare namespace, no type)
///     for every gRPC chain.
///     <para>
///     <c>MapWolverineGrpcServices()</c> eagerly initializes each chain at app-build time — it needs
///     the concrete runtime type to hand to <c>MapGrpcService&lt;T&gt;()</c> — and in a normal
///     <c>Program.cs</c> that runs <i>before</i> <c>RunJasperFxCommands(args)</c> dispatches the
///     codegen command. Every chain's <c>AssembleTypes</c> then short-circuited on a non-null
///     <c>_generatedType</c> left over from that eager pass, even though the codegen command hands it
///     a brand-new <see cref="GeneratedAssembly"/>. Nothing was added, and the written file held
///     nothing but a namespace declaration.
///     </para>
///     <para>
///     Downstream this broke production boots: with <c>TypeLoadMode.Static</c> the missing type
///     surfaced as an <c>ExpectedTypeMissingException</c> at startup, and <c>codegen test</c> was
///     falsely green because it was compiling an empty assembly.
///     </para>
/// </summary>
[Collection(GrpcSerialTestsCollection.Name)]
public class codegen_write_after_mapping_3692
{
    private readonly ITestOutputHelper _output;

    public codegen_write_after_mapping_3692(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task proto_first_chain_still_renders_source_after_mapping()
    {
        await assertEveryFileRendersRealSource(builder =>
        {
            builder.Host.UseWolverine(opts => opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly);

            // Proto-first uses the stock ASP.NET Core gRPC host, not the code-first one.
            builder.Services.AddGrpc();
            builder.Services.AddWolverineGrpc();
        }, "GreeterGrpcHandler");
    }

    [Fact]
    public async Task code_first_and_hand_written_chains_still_render_source_after_mapping()
    {
        await assertEveryFileRendersRealSource(builder =>
        {
            builder.Host.UseWolverine(opts =>
                opts.ApplicationAssembly = typeof(codegen_write_after_mapping_3692).Assembly);

            builder.Services.AddSingleton(new MiddlewareInvocationSink());
            builder.Services.AddCodeFirstGrpc();
            builder.Services.AddWolverineGrpc();
        }, "CodeFirstTestServiceGrpcHandler", "HandWrittenTestGrpcHandler");
    }

    /// <summary>
    ///     Boots a host the way a real application does — mapping the gRPC services (which eagerly
    ///     compiles every chain) before any codegen runs — then replays exactly what
    ///     <c>DynamicCodeBuilder.WriteGeneratedCode</c> does: a fresh <see cref="GeneratedAssembly"/>
    ///     per file, then <c>AssembleTypes</c> and render.
    /// </summary>
    private async Task assertEveryFileRendersRealSource(Action<WebApplicationBuilder> configure,
        params string[] expectedFileNames)
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();
        configure(builder);

        var app = builder.Build();
        app.UseRouting();
        app.MapWolverineGrpcServices();

        await app.StartAsync();

        try
        {
            var graph = app.Services.GetRequiredService<GrpcGraph>();
            var serviceSource = app.Services.GetService<IServiceVariableSource>();

            var rendered = new Dictionary<string, string>();
            foreach (var file in graph.BuildFiles())
            {
                var generatedAssembly = graph.StartAssembly(graph.Rules);
                file.AssembleTypes(generatedAssembly);
                rendered[file.FileName] = generatedAssembly.GenerateCode(serviceSource);
            }

            _output.WriteLine("Generated files: " + rendered.Keys.Join(", "));

            foreach (var expected in expectedFileNames)
            {
                rendered.ContainsKey(expected)
                    .ShouldBeTrue($"expected a generated file named '{expected}'");
            }

            // Not just the named ones — an empty render from *any* chain is the bug.
            foreach (var pair in rendered)
            {
                _output.WriteLine($"===== {pair.Key} =====");
                _output.WriteLine(pair.Value);

                pair.Value.ShouldContain($"class {pair.Key}",
                    customMessage: $"the generated source for '{pair.Key}' declared no type");
            }
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
