using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Grpc;
using Wolverine.Runtime.RemoteInvocation;
using Xunit;

namespace Wolverine.Grpc.Tests;

// GH-2967: inline request/reply over the gRPC transport. The sender configures NO listening endpoint
// and NO reply tracker wiring, so InvokeAsync<T> can only return by reading the reply envelope straight
// off the unary Call(WolverineMessage) response — otherwise it would time out. Mirrors the HTTP test.
[Collection(GrpcSerialTestsCollection.Name)]
public class inline_request_reply_grpc
{
    private const int ReceiverPort = 5188;

    private static Task<IHost> startReceiverAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ListenAtGrpcPort(ReceiverPort);
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<GrpcInlineHandler>();
            })
            .StartAsync();
    }

    private static Task<IHost> startSenderAsync()
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Deliberately NO ListenAtGrpcPort(...).UseForReplies() — the reply rides the gRPC response.
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToGrpcEndpoint("localhost", ReceiverPort);
            })
            .StartAsync();
    }

    [Fact]
    public async Task invoke_reads_reply_from_the_grpc_response_slot()
    {
        using var receiver = await startReceiverAsync();
        using var sender = await startSenderAsync();

        var response = await sender.MessageBus().InvokeAsync<GrpcInlinePong>(new GrpcInlinePing("Rand"));

        response.ShouldNotBeNull();
        response.Name.ShouldBe("Rand");
    }

    [Fact]
    public async Task handler_failure_surfaces_as_request_reply_exception()
    {
        using var receiver = await startReceiverAsync();
        using var sender = await startSenderAsync();

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
            await sender.MessageBus().InvokeAsync<GrpcInlinePong>(new GrpcInlinePing("boom")));

        ex.Message.ShouldContain("boom");
    }
}

public record GrpcInlinePing(string Name);

public record GrpcInlinePong(string Name);

public class GrpcInlineHandler
{
    public GrpcInlinePong Handle(GrpcInlinePing ping)
    {
        if (ping.Name == "boom")
        {
            throw new InvalidOperationException("boom from the gRPC handler");
        }

        return new GrpcInlinePong(ping.Name);
    }
}
