using System.Net;
using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Http.Transport;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using WolverineWebApi;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

// GH-5237. WolverineHttpTransportClientCloudEvents.SendAsync discarded the response, so a send the receiver rejected
// counted as delivered. On the per-envelope /invoke path from GH-5231 that meant a durable outbox deleted an envelope
// that was never received. Every send on both clients now fails on a non-success status.
public class rejected_sends_fail_5237
{
    private const string Uri = "https://svc.test/_wolverine/invoke";

    public static IEnumerable<object[]> Clients()
    {
        yield return [nameof(WolverineHttpTransportClient)];
        yield return [nameof(WolverineHttpTransportClientCloudEvents)];
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task a_rejected_single_send_throws(string clientName)
    {
        var (client, handler) = buildClient(clientName, HttpStatusCode.InternalServerError);

        await Should.ThrowAsync<HttpRequestException>(() => client.SendAsync(Uri, newEnvelope(), new JsonSerializerOptions()));

        handler.Requests.ShouldBe(1);
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task a_rejected_batch_send_throws(string clientName)
    {
        var (client, _) = buildClient(clientName, HttpStatusCode.InternalServerError);
        var batch = new OutgoingMessageBatch(Uri.ToUri(), [newEnvelope()]);

        await Should.ThrowAsync<HttpRequestException>(() => client.SendBatchAsync(Uri, batch));
    }

    [Theory]
    [MemberData(nameof(Clients))]
    public async Task an_accepted_single_send_completes(string clientName)
    {
        var (client, handler) = buildClient(clientName, HttpStatusCode.Accepted);

        await client.SendAsync(Uri, newEnvelope(), new JsonSerializerOptions());

        handler.Requests.ShouldBe(1);
    }

    // The message-loss shape from the issue: a buffered or durable CloudEvents endpoint aimed at /invoke, sending to a
    // receiver that answers 500. The batch has to be reported as failed so it is retried, not marked as sent.
    [Fact]
    public async Task a_rejected_cloud_events_send_to_invoke_fails_the_batch()
    {
        var (client, _) = buildClient(nameof(WolverineHttpTransportClientCloudEvents), HttpStatusCode.InternalServerError);
        var services = new ServiceCollection().AddSingleton(client).BuildServiceProvider();

        var endpoint = new HttpEndpoint(Uri.ToUri(), EndpointRole.Application) { OutboundUri = Uri };
        var protocol = new HttpSenderProtocol(endpoint, services);
        var callback = Substitute.For<ISenderCallback>();
        var batch = new OutgoingMessageBatch(endpoint.Uri, [newEnvelope()]);

        await protocol.SendBatchAsync(callback, batch);

        await callback.Received(1).MarkProcessingFailureAsync(batch, Arg.Any<HttpRequestException>());
        await callback.DidNotReceive().MarkSuccessfulAsync(Arg.Any<OutgoingMessageBatch>());
    }

    // CloudEvents serializes the message itself, so the envelope needs one
    private static Envelope newEnvelope() => new(new HttpMessage1("rejected")) { Id = Guid.NewGuid(), Data = [1] };

    private static (IWolverineHttpTransportClient, StatusHandler) buildClient(string clientName, HttpStatusCode status)
    {
        var handler = new StatusHandler(status);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, false));

        IWolverineHttpTransportClient client = clientName == nameof(WolverineHttpTransportClient)
            ? new WolverineHttpTransportClient(factory)
            : new WolverineHttpTransportClientCloudEvents(factory);

        return (client, handler);
    }

    public class StatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }
}
