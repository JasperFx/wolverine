using System.Text.Json;
using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Http.Transport;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

// GH-5231. HttpEndpoint inherits Endpoint's default EndpointMode.BufferedInMemory, so ToHttpEndpoint(".../_wolverine/invoke")
// without .SendInline() posted "binary/wolverine-envelopes" batches to the /invoke route, which takes one envelope and
// answers a batch with a 415. The sender logged "Failure trying to send a message batch" and retried forever; nothing was
// ever delivered. A runtime-resolved destination (EndpointFor(uri) / ToDestination(uri)) could not be given .SendInline()
// at all. A non-inline endpoint aimed at /invoke now posts each envelope of a batch on its own.
public record InvokeRouteProbe(string Name);

public static class InvokeRouteProbeHandler
{
    public static TaskCompletionSource<string> Received { get; set; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static void Handle(InvokeRouteProbe message) => Received.TrySetResult(message.Name);
}

public class buffered_endpoint_aimed_at_invoke_5231
{
    [Theory]
    [InlineData("https://svc.test/_wolverine/invoke", true)]
    [InlineData("https://svc.test/_wolverine/invoke/", true)]
    [InlineData("https://svc.test/custom/prefix/invoke", true)]
    [InlineData("https://svc.test/_wolverine/batch/one", false)]
    [InlineData("https://svc.test/api", false)]
    [InlineData("https://svc.test/invoker", false)]
    public void recognizes_the_invoke_route(string outboundUri, bool expected)
    {
        var endpoint = new HttpEndpoint(outboundUri.ToUri(), EndpointRole.Application) { OutboundUri = outboundUri };
        endpoint.TargetsInvokeRoute.ShouldBe(expected);
    }

    [Fact]
    public async Task a_batch_aimed_at_invoke_is_sent_one_envelope_at_a_time()
    {
        var (protocol, client, callback, batch) = buildProtocol("https://svc.test/_wolverine/invoke");

        await protocol.SendBatchAsync(callback, batch);

        await client.DidNotReceive().SendBatchAsync(Arg.Any<string>(), Arg.Any<OutgoingMessageBatch>());
        foreach (var envelope in batch.Messages)
        {
            await client.Received(1).SendAsync("https://svc.test/_wolverine/invoke",
                Arg.Is<Envelope>(e => e.Id == envelope.Id), Arg.Any<JsonSerializerOptions>());
        }

        await callback.Received(1).MarkSuccessfulAsync(batch);
    }

    [Fact]
    public async Task a_batch_aimed_at_the_batch_route_is_still_one_post()
    {
        var (protocol, client, callback, batch) = buildProtocol("https://svc.test/_wolverine/batch/one");

        await protocol.SendBatchAsync(callback, batch);

        await client.Received(1).SendBatchAsync("https://svc.test/_wolverine/batch/one", batch);
        await client.DidNotReceive().SendAsync(Arg.Any<string>(), Arg.Any<Envelope>(), Arg.Any<JsonSerializerOptions>());
        await callback.Received(1).MarkSuccessfulAsync(batch);
    }

    [Fact]
    public async Task a_failed_invoke_send_fails_the_batch_so_it_is_retried()
    {
        var (protocol, client, callback, batch) = buildProtocol("https://svc.test/_wolverine/invoke");
        var failure = new HttpRequestException("Response status code does not indicate success: 500");
        client.SendAsync(Arg.Any<string>(), Arg.Any<Envelope>(), Arg.Any<JsonSerializerOptions>())
            .Returns(Task.FromException(failure));

        await protocol.SendBatchAsync(callback, batch);

        await callback.Received(1).MarkProcessingFailureAsync(batch, failure);
        await callback.DidNotReceive().MarkSuccessfulAsync(Arg.Any<OutgoingMessageBatch>());
    }

    // The reporter's shape, over real HTTP: a default-mode (buffered) endpoint aimed at /_wolverine/invoke with no
    // .SendInline(), sending to a receiver that maps the standard transport routes.
    [Fact]
    public async Task a_default_mode_endpoint_aimed_at_invoke_delivers()
    {
        InvokeRouteProbeHandler.Received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(InvokeRouteProbeHandler));
        });
        builder.Services.AddWolverineHttp();

        await using var receiver = builder.Build();
        receiver.MapWolverineHttpTransportEndpoints();
        await receiver.StartAsync(TestContext.Current.CancellationToken);

        const string invokeUrl = "http://receiver.test/_wolverine/invoke";
        var testServer = receiver.GetTestServer();

        using var sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.PublishMessage<InvokeRouteProbe>().ToHttpEndpoint(invokeUrl);
                opts.Services.AddWolverineHttp();
                opts.Services.AddHttpClient(invokeUrl, c => c.BaseAddress = new Uri(invokeUrl))
                    .ConfigurePrimaryHttpMessageHandler(() => testServer.CreateHandler());
            }).StartAsync(TestContext.Current.CancellationToken);

        var endpoint = sender.GetRuntime().Options.Transports.GetOrCreate<HttpTransport>().EndpointFor(invokeUrl);
        endpoint.Mode.ShouldBe(EndpointMode.BufferedInMemory);

        await sender.MessageBus().PublishAsync(new InvokeRouteProbe("Min Farshaw"));

        var name = await InvokeRouteProbeHandler.Received.Task.WaitAsync(TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);
        name.ShouldBe("Min Farshaw");
    }

    private static (HttpSenderProtocol, IWolverineHttpTransportClient, ISenderCallback, OutgoingMessageBatch)
        buildProtocol(string outboundUri)
    {
        var endpoint = new HttpEndpoint(outboundUri.ToUri(), EndpointRole.Application) { OutboundUri = outboundUri };

        var client = Substitute.For<IWolverineHttpTransportClient>();
        var services = new ServiceCollection().AddSingleton(client).BuildServiceProvider();

        var batch = new OutgoingMessageBatch(endpoint.Uri, new[]
        {
            new Envelope { Id = Guid.NewGuid(), Data = [1] },
            new Envelope { Id = Guid.NewGuid(), Data = [2] }
        });

        return (new HttpSenderProtocol(endpoint, services), client, Substitute.For<ISenderCallback>(), batch);
    }
}
