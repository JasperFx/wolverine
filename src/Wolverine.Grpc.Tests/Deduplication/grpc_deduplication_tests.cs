using Grpc.Core;
using Microsoft.Extensions.DependencyInjection;
using ProtoBuf.Grpc;
using Shouldly;
using Xunit;

namespace Wolverine.Grpc.Tests.Deduplication;

/// <summary>
///     GH-4180. Logical deduplication over Wolverine gRPC services.
///
///     <para>
///     A gRPC method owes its caller a status, so a refusal is an <c>RpcException</c> rather than the
///     silent discard a message handler gets or the problem document an HTTP endpoint gets. The codes
///     come from <see href="https://google.aip.dev/193">AIP-193</see>, the same table
///     <c>WolverineGrpcExceptionInterceptor</c> already maps ordinary exceptions through.
///     </para>
/// </summary>
public class grpc_deduplication_tests
{
    private static CallContext WithKey(string? key)
    {
        if (key == null) return new CallContext();

        // gRPC lower-cases metadata keys on the wire; Metadata refuses a key with upper-case
        // characters outright, which is exactly why the generated read lower-cases the configured
        // header name rather than trusting it.
        var headers = new Metadata { { "idempotency-key", key } };
        return new CallContext(new CallOptions(headers));
    }

    [Fact]
    public async Task the_second_call_with_the_same_key_is_refused_with_already_exists()
    {
        await using var host = await DeduplicationGrpcHost.StartAsync();
        var client = host.CreateClient<IDeduplicatedEchoService>();

        DedupEchoHandler.Received.Clear();

        var first = await client.Echo(new DedupEchoRequest { Name = "first" }, WithKey("order-1"));
        first.Name.ShouldBe("first");

        // Same key, different payload. Nothing but the logical id can refuse this one.
        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.Echo(new DedupEchoRequest { Name = "second" }, WithKey("order-1")));

        ex.StatusCode.ShouldBe(StatusCode.AlreadyExists);

        DedupEchoHandler.Received.ShouldHaveSingleItem().ShouldBe("first");
        host.Deduplicator.Claims.ShouldBe(["order-1", "order-1"]);
    }

    [Fact]
    public async Task different_keys_both_run()
    {
        await using var host = await DeduplicationGrpcHost.StartAsync();
        var client = host.CreateClient<IDeduplicatedEchoService>();

        DedupEchoHandler.Received.Clear();

        await client.Echo(new DedupEchoRequest { Name = "a" }, WithKey("order-a"));
        await client.Echo(new DedupEchoRequest { Name = "b" }, WithKey("order-b"));

        DedupEchoHandler.Received.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task a_missing_required_key_is_invalid_argument_rather_than_a_silent_pass()
    {
        await using var host = await DeduplicationGrpcHost.StartAsync();
        var client = host.CreateClient<IDeduplicatedEchoService>();

        DedupEchoHandler.Received.Clear();

        var ex = await Should.ThrowAsync<RpcException>(async () =>
            await client.Echo(new DedupEchoRequest { Name = "no key" }, WithKey(null)));

        ex.StatusCode.ShouldBe(StatusCode.InvalidArgument);

        // Nothing ran, and nothing was claimed -- a missing id is the opposite of a duplicate.
        DedupEchoHandler.Received.ShouldBeEmpty();
        host.Deduplicator.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_optional_key_lets_unkeyed_calls_straight_through()
    {
        await using var host = await DeduplicationGrpcHost.StartAsync();
        var client = host.CreateClient<IDeduplicatedEchoService>();

        DedupEchoHandler.Received.Clear();

        await client.EchoOptional(new DedupEchoRequest { Name = "x" }, WithKey(null));
        await client.EchoOptional(new DedupEchoRequest { Name = "y" }, WithKey(null));

        DedupEchoHandler.Received.ShouldBe(["x", "y"]);

        // And no database round trip was paid for either of them -- the generated claim is guarded by
        // a null check rather than called unconditionally.
        host.Deduplicator.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task an_unguarded_method_on_the_same_service_is_untouched()
    {
        // The requirement is resolved per RPC method, not per chain. A gRPC chain is a whole service,
        // so a chain-level requirement would force "all of this service's calls or none of them".
        await using var host = await DeduplicationGrpcHost.StartAsync();
        var client = host.CreateClient<IDeduplicatedEchoService>();

        DedupEchoHandler.Received.Clear();

        await client.EchoUnguarded(new DedupEchoRequest { Name = "one" }, WithKey("shared-key"));
        await client.EchoUnguarded(new DedupEchoRequest { Name = "two" }, WithKey("shared-key"));

        DedupEchoHandler.Received.ShouldBe(["one", "two"]);
        host.Deduplicator.Claims.ShouldBeEmpty();
    }

    [Fact]
    public async Task the_generated_code_weaves_deduplication_into_only_the_attributed_methods()
    {
        await using var host = await DeduplicationGrpcHost.StartAsync();

        var graph = host.Services.GetRequiredService<GrpcGraph>();
        var chain = graph.CodeFirstChains.Single(c => c.ServiceContractType == typeof(IDeduplicatedEchoService));

        var source = chain.SourceCode;
        source.ShouldNotBeNull();

        // Two of the three RPCs are attributed, so exactly two claims are generated. Asserting the
        // COUNT rather than mere presence is what catches a chain-level requirement leaking onto
        // EchoUnguarded, which no behavioural test above would notice if the claim always returned true.
        (source!.Split("TryClaimAsync").Length - 1).ShouldBe(2);

        // The id comes out of request metadata, lower-cased, because gRPC lower-cases keys on the wire
        source.ShouldContain("RequestHeaders?.GetValue(\"idempotency-key\")");

        // Only the Required = true method generates the missing-id guard
        (source.Split("missingDeduplicationId").Length - 1).ShouldBe(2);

        // ...and every deduplicated method compensates on failure, because a gRPC service method is
        // never in a Wolverine transaction of its own
        (source.Split("ReleaseAsync").Length - 1).ShouldBe(2);
    }
}
