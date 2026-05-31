using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Runtime.RemoteInvocation;
using Wolverine.Runtime.Serialization;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

// GH-2966: the sender side of optimized inline request/reply over the Wolverine.Http transport.
// The message has NO local handler and the host configures NO listening endpoint and registers NO
// ReplyListener — so the only way InvokeAsync<T> can return (rather than time out) is by reading the
// reply straight off the HTTP transport's response slot, which is what HttpEndpoint
// (IInlineRequestReplyEndpoint) does. A fake IWolverineHttpTransportClient stands in for the receiver.
public record InlineProbeRequest(string Name);

public record InlineProbeResponse(string Name);

public class inline_request_reply_sender
{
    private static IHostBuilder ConfigureSender(IWolverineHttpTransportClient client)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishAllMessages().ToHttpEndpoint("https://receiver.test/_wolverine/invoke");
                opts.Services.AddSingleton(client);
            });
    }

    [Fact]
    public async Task invoke_reads_reply_from_the_http_response_slot()
    {
        using var host = await ConfigureSender(new EchoingInlineClient()).StartAsync();

        var response = await host.MessageBus().InvokeAsync<InlineProbeResponse>(new InlineProbeRequest("Egwene"));

        response.ShouldNotBeNull();
        response.Name.ShouldBe("Egwene");
    }

    [Fact]
    public async Task handler_failure_surfaces_as_request_reply_exception()
    {
        using var host = await ConfigureSender(new FailingInlineClient()).StartAsync();

        var ex = await Should.ThrowAsync<WolverineRequestReplyException>(async () =>
            await host.MessageBus().InvokeAsync<InlineProbeResponse>(new InlineProbeRequest("Nynaeve")));

        ex.Message.ShouldContain("kaboom");
    }

    // Simulates the receiver: echoes the request name back as an InlineProbeResponse reply envelope.
    private class EchoingInlineClient : IWolverineHttpTransportClient
    {
        public Task SendBatchAsync(string uri, OutgoingMessageBatch batch) => Task.CompletedTask;
        public Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions serializerOptions) => Task.CompletedTask;

        public Task<InlineHttpReply> InvokeAsync(string uri, Envelope envelope, JsonSerializerOptions serializerOptions)
        {
            var request = JsonSerializer.Deserialize<InlineProbeRequest>(envelope.Data!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

            var serializer = new SystemTextJsonSerializer(new JsonSerializerOptions());
            var response = new InlineProbeResponse(request.Name);
            var reply = new Envelope(response) { Serializer = serializer, ContentType = serializer.ContentType };
            reply.Data = serializer.WriteMessage(response);

            var bytes = EnvelopeSerializer.Serialize(reply);
            return Task.FromResult(new InlineHttpReply(200, bytes));
        }
    }

    // Simulates a receiver whose handler threw: replies 500 with an envelope-shaped FailureAcknowledgement.
    private class FailingInlineClient : IWolverineHttpTransportClient
    {
        public Task SendBatchAsync(string uri, OutgoingMessageBatch batch) => Task.CompletedTask;
        public Task SendAsync(string uri, Envelope envelope, JsonSerializerOptions serializerOptions) => Task.CompletedTask;

        public Task<InlineHttpReply> InvokeAsync(string uri, Envelope envelope, JsonSerializerOptions serializerOptions)
        {
            var failure = new FailureAcknowledgement { RequestId = envelope.Id, Message = "kaboom" };
            var reply = new Envelope(failure) { Serializer = IntrinsicSerializer.Instance, ContentType = IntrinsicSerializer.Instance.ContentType };
            reply.Data = IntrinsicSerializer.Instance.Write(reply);

            var bytes = EnvelopeSerializer.Serialize(reply);
            return Task.FromResult(new InlineHttpReply(500, bytes));
        }
    }
}
