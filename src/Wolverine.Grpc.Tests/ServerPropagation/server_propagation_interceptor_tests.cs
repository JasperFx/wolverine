using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc.Client;
using Shouldly;
using Wolverine.Grpc.Client;
using Wolverine.Grpc.Tests.Client;
using Xunit;

namespace Wolverine.Grpc.Tests.ServerPropagation;

/// <summary>
///     End-to-end tests for <see cref="WolverineGrpcServicePropagationInterceptor"/>. Unlike
///     <see cref="Client.propagation_interceptor_tests"/> (which only asserts what lands on the
///     wire), these assert what the invoked Wolverine <em>handler</em> actually sees on
///     <see cref="IMessageContext"/> after both the client-side and server-side interceptors have
///     run — this is the full Wolverine-to-Wolverine hop the OrderChainWithGrpc sample and the
///     gRPC client docs describe.
/// </summary>
[Collection("grpc-client")]
public class server_propagation_interceptor_tests
{
    private readonly WolverineGrpcClientFixture _fixture;

    public server_propagation_interceptor_tests(WolverineGrpcClientFixture fixture)
    {
        _fixture = fixture;
    }

    private ServiceProvider BuildContainer(TestMessageContext? ctx)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        if (ctx != null)
        {
            services.AddScoped<IMessageContext>(_ => ctx);
            services.AddScoped<IMessageBus>(sp => sp.GetRequiredService<IMessageContext>());
        }

        services.AddWolverineGrpcClient<IPropagationEchoService>(o =>
        {
            o.Address = new Uri("http://localhost");
        }).ConfigureChannel(c => c.HttpHandler = _fixture.ServerHandler);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task handler_sees_correlation_and_tenant_id_propagated_from_the_caller()
    {
        var ctx = new TestMessageContext
        {
            CorrelationId = "corr-hop-1",
            TenantId = "tenant-hop-1"
        };

        await using var provider = BuildContainer(ctx);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IPropagationEchoService>();

        var reply = await client.Echo(new PropagationEchoRequest());

        // These values only round-trip if the client interceptor stamped the headers AND the
        // server interceptor read them back onto the ambient IMessageContext BEFORE Bus.InvokeAsync
        // ran the handler — proving the full hop, not just the wire format.
        reply.CorrelationId.ShouldBe("corr-hop-1");
        reply.TenantId.ShouldBe("tenant-hop-1");
    }

    [Fact]
    public async Task handler_does_not_see_a_specific_tenant_id_when_caller_has_no_message_context_in_scope()
    {
        await using var provider = BuildContainer(ctx: null);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IPropagationEchoService>();

        var reply = await client.Echo(new PropagationEchoRequest());

        // Neither field is asserted as null/empty here: MessageBus always assigns a CorrelationId
        // from Activity.Current?.RootId (or a fresh GUID), and an untenanted context reads back
        // Wolverine's internal default-tenant sentinel rather than null — neither is something this
        // interceptor controls or suppresses. What it does control is whether a *specific* caller
        // value shows up uninvited, which is what the round-trip tests above already prove positively.
        reply.TenantId.ShouldNotBe("tenant-hop-1");
        reply.TenantId.ShouldNotBe("tenant-only");
    }

    [Fact]
    public async Task tenant_id_alone_propagates_without_a_correlation_id_header()
    {
        // TestMessageContext's parameterless constructor auto-generates a CorrelationId — clear
        // it explicitly so this test isolates tenant-id propagation on its own (no correlation-id
        // header goes out on the wire).
        var ctx = new TestMessageContext
        {
            CorrelationId = null,
            TenantId = "tenant-only"
        };

        await using var provider = BuildContainer(ctx);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IPropagationEchoService>();

        var reply = await client.Echo(new PropagationEchoRequest());

        reply.TenantId.ShouldBe("tenant-only");
    }

    [Fact]
    public async Task an_arbitrary_non_wolverine_caller_can_also_drive_tenant_id_via_the_raw_header()
    {
        // No AddWolverineGrpcClient, no IMessageContext anywhere in this process — an ordinary
        // grpc-dotnet caller setting the header by hand, same as an external service or gateway
        // would. This is the scenario from the original Discord question: does something read
        // an inbound tenant-id header at all? It does, independent of who sent it.
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = _fixture.ServerHandler
        });
        var client = channel.CreateGrpcService<IPropagationEchoService>();

        var headers = new Metadata
        {
            { EnvelopeConstants.TenantIdKey, "external-caller-tenant" }
        };

        var reply = await client.Echo(new PropagationEchoRequest(), new CallOptions(headers: headers));

        reply.TenantId.ShouldBe("external-caller-tenant");
    }
}
