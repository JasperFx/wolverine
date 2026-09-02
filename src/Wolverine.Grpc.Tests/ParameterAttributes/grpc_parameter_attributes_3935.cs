using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Server;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.ParameterAttributes;

/// <summary>
/// GH-3935: none of Wolverine's <see cref="Wolverine.Attributes.WolverineParameterAttribute"/> family
/// worked on gRPC services. <c>TryApply</c> is called from <c>HandlerChain.configureFrames</c> for
/// message handlers and from <c>HttpChainParameterAttributeStrategy</c> for HTTP endpoints, and
/// nothing in Wolverine.Grpc called it at all.
///
/// <para>Scope is the user-authored before/after hooks, which are the only place on a gRPC service
/// with a parameter list somebody can decorate. The RPC methods themselves stay out: their signatures
/// are proto-defined, and the answer there is the downstream message handler, which has supported the
/// whole family all along.</para>
///
/// <para>These assertions are on the generated source, so they cannot be satisfied by anything at
/// runtime -- the substituted variable either reached the emitted call or it did not.</para>
/// </summary>
[Collection("GrpcSerialTests")]
public class grpc_parameter_attributes_3935
{
    private static async Task<WebApplication> startAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.WebHost.UseTestServer();

        builder.Host.UseWolverine(opts =>
        {
            opts.ApplicationAssembly = typeof(GH3935ValueAttribute).Assembly;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Discovery.IncludeType(typeof(GH3935CodeFirstHandler));
            opts.Discovery.IncludeType(typeof(GH3935ProtoFirstHandler));
        });

        builder.Services.AddCodeFirstGrpc();
        builder.Services.AddWolverineGrpc();

        var app = builder.Build();
        app.UseRouting();

        // This is what drives AssembleTypes and therefore populates SourceCode -- discovery alone
        // does not compile anything.
        app.MapWolverineGrpcServices();

        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task applies_parameter_attributes_to_proto_first_hooks()
    {
        await using var host = await startAsync();
        var graph = host.Services.GetRequiredService<GrpcGraph>();

        var chain = graph.Chains.Single(c => c.StubType == typeof(ParameterAttributeStub));

        chain.SourceCode.ShouldNotBeNull();

        // Both hooks are called with the attribute's substituted value rather than a resolved
        // service or a compile error.
        chain.SourceCode!.ShouldContain(
            $"{nameof(ParameterAttributeStub.BeforeWithParameterAttribute)}(\"{GH3935ValueAttribute.Marker}\")");
        chain.SourceCode.ShouldContain(
            $"{nameof(ParameterAttributeStub.AfterWithParameterAttribute)}(\"{GH3935ValueAttribute.Marker}\")");
    }

    [Fact]
    public async Task applies_parameter_attributes_to_code_first_hooks()
    {
        await using var host = await startAsync();
        var graph = host.Services.GetRequiredService<GrpcGraph>();

        var chain = graph.CodeFirstChains
            .Single(c => c.ServiceContractType == typeof(IGH3935CodeFirstService));

        chain.SourceCode.ShouldNotBeNull();
        chain.SourceCode!.ShouldContain($"Before(\"{GH3935ValueAttribute.Marker}\")");
        chain.SourceCode.ShouldContain($"After(\"{GH3935ValueAttribute.Marker}\")");
    }

    [Fact]
    public async Task applies_parameter_attributes_to_hand_written_hooks()
    {
        await using var host = await startAsync();
        var graph = host.Services.GetRequiredService<GrpcGraph>();

        var chain = graph.HandWrittenChains
            .Single(c => c.ServiceClassType == typeof(GH3935HandWrittenGrpcService));

        chain.SourceCode.ShouldNotBeNull();
        chain.SourceCode!.ShouldContain($"Before(\"{GH3935ValueAttribute.Marker}\")");
        chain.SourceCode.ShouldContain($"After(\"{GH3935ValueAttribute.Marker}\")");
    }

    [Fact]
    public async Task leaves_the_rpc_methods_themselves_alone()
    {
        // The deliberate non-goal. A proto-defined RPC signature has nowhere to hang an attribute,
        // and the forwarding call must keep passing the real request through rather than anything a
        // parameter attribute produced.
        await using var host = await startAsync();
        var graph = host.Services.GetRequiredService<GrpcGraph>();

        var chain = graph.Chains.Single(c => c.StubType == typeof(ParameterAttributeStub));

        var code = chain.SourceCode!;
        code.ShouldContain("InvokeAsync");
        code.ShouldNotContain($"InvokeAsync<Generated.ParamReply>(\"{GH3935ValueAttribute.Marker}\"");
    }
}
