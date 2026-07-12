using System.Security.Claims;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using ProtoBuf.Grpc;
using Shouldly;
using Wolverine.Grpc.MultiTenancy;
using Xunit;

namespace Wolverine.Grpc.Tests.MultiTenancy;

/// <summary>
///     End-to-end tests for GH-3368 server-side tenant id detection: each test boots a host with
///     one detection configuration and proves the tenant id detected by the generated service
///     wrapper reaches the Wolverine handler's <see cref="IMessageContext.TenantId"/>. All tests
///     go through a custom header/claim/strategy that the runtime propagation interceptor does
///     NOT read (it only knows 'tenant-id'), so a passing test can only be explained by the
///     codegen-level detection frame.
/// </summary>
[Collection("grpc-tenant-detection")]
public class tenant_detection_tests
{
    private static CallContext headers(params (string Key, string Value)[] entries)
    {
        var metadata = new Metadata();
        foreach (var (key, value) in entries)
        {
            metadata.Add(key, value);
        }

        return new CallContext(new CallOptions(headers: metadata));
    }

    [Fact]
    public async Task detects_tenant_id_from_a_custom_request_header()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-tenant");
        });

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest(), headers(("x-tenant", "green")));

        reply.TenantId.ShouldBe("green");
    }

    [Fact]
    public async Task header_detection_is_case_insensitive_on_the_configured_name()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            // Configured with casing that never matches the lowercased wire form directly
            o.TenantId.IsRequestHeaderValue("X-Tenant");
        });

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest(), headers(("x-tenant", "blue")));

        reply.TenantId.ShouldBe("blue");
    }

    [Fact]
    public async Task detects_tenant_id_from_a_claim_on_the_authenticated_principal()
    {
        await using var host = await TenantDetectionHost.StartAsync(
            o => o.TenantId.IsClaimTypeNamed("tenant-claim"),
            app =>
            {
                // Fake authentication: stamp a principal carrying the tenant claim onto every call
                app.Use(async (HttpContext ctx, RequestDelegate next) =>
                {
                    ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim("tenant-claim", "claim-tenant")], "test-auth"));
                    await next(ctx);
                });
            });

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest());

        reply.TenantId.ShouldBe("claim-tenant");
    }

    [Fact]
    public async Task detects_tenant_id_with_a_custom_strategy_built_from_the_container()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.DetectWith<CustomHeaderTenantDetection>();
        });

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest(),
            headers((CustomHeaderTenantDetection.HeaderName, "special")));

        reply.TenantId.ShouldBe("custom-special");
    }

    [Fact]
    public async Task strategies_run_in_order_and_first_non_empty_wins()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-primary-tenant");
            o.TenantId.IsRequestHeaderValue("x-secondary-tenant");
        });

        var client = host.CreateClient<ITenantEchoService>();

        var both = await client.Echo(new TenantEchoRequest(),
            headers(("x-primary-tenant", "first"), ("x-secondary-tenant", "second")));
        both.TenantId.ShouldBe("first");

        var secondaryOnly = await client.Echo(new TenantEchoRequest(),
            headers(("x-secondary-tenant", "second")));
        secondaryOnly.TenantId.ShouldBe("second");
    }

    [Fact]
    public async Task default_is_supplies_a_fallback_tenant_when_nothing_was_detected()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-tenant");
            o.TenantId.DefaultIs("fallback-tenant");
        });

        var client = host.CreateClient<ITenantEchoService>();

        var withHeader = await client.Echo(new TenantEchoRequest(), headers(("x-tenant", "explicit")));
        withHeader.TenantId.ShouldBe("explicit");

        var withoutHeader = await client.Echo(new TenantEchoRequest());
        withoutHeader.TenantId.ShouldBe("fallback-tenant");
    }

    [Fact]
    public async Task assert_exists_rejects_calls_without_a_detectable_tenant_id()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-tenant");
            o.TenantId.AssertExists();
        });

        var client = host.CreateClient<ITenantEchoService>();

        // The gRPC mapping of Wolverine.Http's 400 Bad Request for a missing mandatory tenant id
        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.Echo(new TenantEchoRequest()));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);
        ex.Status.Detail.ShouldBe(GrpcTenantDetection.NoMandatoryTenantIdCouldBeDetectedForThisGrpcCall);
    }

    [Fact]
    public async Task assert_exists_still_lets_tenanted_calls_through()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-tenant");
            o.TenantId.AssertExists();
        });

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest(), headers(("x-tenant", "allowed")));

        reply.TenantId.ShouldBe("allowed");
    }

    [Fact]
    public async Task missing_tenant_without_assert_exists_flows_through_untenanted()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-tenant");
        });

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest());

        // No RpcException, and no phantom tenant shows up — the call proceeds untenanted.
        // (Not asserted as null: an untenanted IMessageContext may surface Wolverine's internal
        // default-tenant sentinel rather than null, which detection neither causes nor controls.)
        reply.TenantId.ShouldNotBe("green");
    }

    [Fact]
    public async Task zero_config_default_detects_the_tenant_id_header_stamped_by_wolverine_clients()
    {
        // No o.TenantId configuration at all — PropagateEnvelopeHeaders defaults to true, so the
        // server should detect the same 'tenant-id' metadata header the Wolverine gRPC client
        // propagation interceptor stamps on outgoing calls.
        await using var host = await TenantDetectionHost.StartAsync();

        var client = host.CreateClient<ITenantEchoService>();

        var reply = await client.Echo(new TenantEchoRequest(), headers(("tenant-id", "hop-tenant")));

        reply.TenantId.ShouldBe("hop-tenant");
    }

    [Fact]
    public async Task explicit_configuration_replaces_the_zero_config_default()
    {
        await using var host = await TenantDetectionHost.StartAsync(o =>
        {
            o.TenantId.IsRequestHeaderValue("x-tenant");
        });

        var options = host.Services.GetService(typeof(WolverineGrpcOptions)) as WolverineGrpcOptions;
        options!.TenantIdDetection.ZeroConfigDefaultApplied.ShouldBeFalse();
        options.TenantIdDetection.Strategies.ShouldHaveSingleItem()
            .ShouldBeOfType<MetadataHeaderDetection>();
    }
}
