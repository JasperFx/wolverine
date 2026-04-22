using GreeterProtoFirstGrpc.Server;
using JasperFx;
using JasperFx.CodeGeneration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Xunit;

namespace Wolverine.Grpc.Tests;

/// <summary>
///     Verifies that <see cref="GrpcServiceChain"/> reports <c>MiddlewareScoping.Grpc</c> so that
///     <c>[WolverineBefore(MiddlewareScoping.Grpc)]</c> attaches exclusively to gRPC chains and
///     <c>[WolverineBefore(MiddlewareScoping.MessageHandlers)]</c> no longer inadvertently attaches
///     (the behavior correction introduced alongside the new enum value).
/// </summary>
public class grpc_middleware_scoping_tests
{
    [Fact]
    public async Task grpc_service_chain_reports_grpc_scoping()
    {
        var chain = await DiscoverGreeterChainAsync();
        chain.Scoping.ShouldBe(MiddlewareScoping.Grpc);
    }

    [Fact]
    public async Task grpc_scoped_middleware_applies_to_grpc_chain()
    {
        var chain = await DiscoverGreeterChainAsync();
        var method = typeof(ScopingFixture).GetMethod(nameof(ScopingFixture.GrpcOnly))!;
        chain.MatchesScope(method).ShouldBeTrue();
    }

    [Fact]
    public async Task anywhere_scoped_middleware_still_applies_to_grpc_chain()
    {
        var chain = await DiscoverGreeterChainAsync();
        var method = typeof(ScopingFixture).GetMethod(nameof(ScopingFixture.Anywhere))!;
        chain.MatchesScope(method).ShouldBeTrue();
    }

    [Fact]
    public async Task message_handlers_scoped_middleware_no_longer_applies_to_grpc_chain()
    {
        var chain = await DiscoverGreeterChainAsync();
        var method = typeof(ScopingFixture).GetMethod(nameof(ScopingFixture.MessageHandlersOnly))!;
        chain.MatchesScope(method).ShouldBeFalse(
            "gRPC chains must not pick up middleware that was explicitly scoped to MessageHandlers");
    }

    private static async Task<GrpcServiceChain> DiscoverGreeterChainAsync()
    {
        DynamicCodeBuilder.WithinCodegenCommand = true;
        try
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.ApplicationAssembly = typeof(GreeterGrpcService).Assembly;
                })
                .ConfigureServices(services => services.AddWolverineGrpc())
                .StartAsync();

            var graph = host.Services.GetRequiredService<GrpcGraph>();
            var grpcOptions = host.Services.GetRequiredService<WolverineGrpcOptions>();
            graph.DiscoverServices(grpcOptions);
            return graph.Chains.ShouldHaveSingleItem();
        }
        finally
        {
            DynamicCodeBuilder.WithinCodegenCommand = false;
        }
    }

    private static class ScopingFixture
    {
        [WolverineBefore(MiddlewareScoping.Grpc)]
        public static void GrpcOnly() { }

        [WolverineBefore]
        public static void Anywhere() { }

        [WolverineBefore(MiddlewareScoping.MessageHandlers)]
        public static void MessageHandlersOnly() { }
    }
}
