using Microsoft.Extensions.DependencyInjection;
using PingPongWithGrpc.Messages;
using Shouldly;
using Wolverine.Grpc.Client;
using Xunit;

namespace Wolverine.Grpc.Tests.Client;

/// <summary>
///     End-to-end tests for <see cref="WolverineGrpcClientPropagationInterceptor"/>. Each test
///     builds an isolated DI container with a fake <see cref="TestMessageContext"/> registered
///     as scoped <see cref="IMessageContext"/> — the interceptor reads from whatever the
///     container resolves, so a fake is sufficient to observe which fields land on the wire.
/// </summary>
[Collection("grpc-client")]
public class propagation_interceptor_tests
{
    private readonly WolverineGrpcClientFixture _fixture;

    public propagation_interceptor_tests(WolverineGrpcClientFixture fixture)
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

        services.AddWolverineGrpcClient<IHeaderEchoService>(o =>
        {
            o.Address = new Uri("http://localhost");
        }).ConfigureChannel(c => c.HttpHandler = _fixture.ServerHandler);

        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task stamps_correlation_and_tenant_headers_when_message_context_is_in_scope()
    {
        var ctx = new TestMessageContext
        {
            CorrelationId = "corr-abc-123",
            TenantId = "tenant-42"
        };

        await using var provider = BuildContainer(ctx);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IHeaderEchoService>();

        var reply = await client.Echo(new HeaderEchoRequest());

        reply.CorrelationId.ShouldBe("corr-abc-123");
        reply.TenantId.ShouldBe("tenant-42");
    }

    [Fact]
    public async Task stamps_envelope_derived_headers_when_context_has_an_envelope()
    {
        // TestMessageContext's Envelope is created from the supplied message — envelope setters are
        // public on Envelope itself, so we mutate it post-construction rather than reaching into
        // TestMessageContext's private seams.
        var message = new PingRequest();
        var ctx = new TestMessageContext(message) { CorrelationId = "corr-1" };
        var envelope = ctx.Envelope!;
        envelope.ConversationId = Guid.NewGuid();
        envelope.ParentId = "parent-operation-99";

        await using var provider = BuildContainer(ctx);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IHeaderEchoService>();

        var reply = await client.Echo(new HeaderEchoRequest());

        reply.MessageId.ShouldBe(envelope.Id.ToString());
        reply.ConversationId.ShouldBe(envelope.ConversationId.ToString());
        reply.ParentId.ShouldBe("parent-operation-99");
    }

    [Fact]
    public async Task silently_no_ops_when_no_message_context_is_in_scope()
    {
        // Bare Program.cs scenario — there is no IMessageContext registered. The interceptor must
        // not throw; the call goes through without Wolverine headers. Plan §5.1 design note.
        await using var provider = BuildContainer(ctx: null);
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IHeaderEchoService>();

        var reply = await client.Echo(new HeaderEchoRequest());

        reply.CorrelationId.ShouldBeNull();
        reply.TenantId.ShouldBeNull();
        reply.MessageId.ShouldBeNull();
        reply.ConversationId.ShouldBeNull();
        reply.ParentId.ShouldBeNull();
    }

    [Fact]
    public async Task propagation_can_be_disabled_per_client()
    {
        var ctx = new TestMessageContext
        {
            CorrelationId = "should-not-propagate",
            TenantId = "tenant-suppressed"
        };

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<IMessageContext>(_ => ctx);
        services.AddScoped<IMessageBus>(sp => sp.GetRequiredService<IMessageContext>());

        services.AddWolverineGrpcClient<IHeaderEchoService>(o =>
        {
            o.Address = new Uri("http://localhost");
            o.PropagateEnvelopeHeaders = false;
        }).ConfigureChannel(c => c.HttpHandler = _fixture.ServerHandler);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IHeaderEchoService>();

        var reply = await client.Echo(new HeaderEchoRequest());

        reply.CorrelationId.ShouldBeNull();
        reply.TenantId.ShouldBeNull();
    }
}
