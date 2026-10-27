using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Runtime.Serialization;
using Wolverine.Tracking;
using Wolverine.Util;
using WolverineWebApi;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

// GH-5238. The HTTP transport was only registered by ToHttpEndpoint(), so an http(s) URI that reached Wolverine any
// other way failed with "Unknown Transport scheme 'http'". UseHttpTransport() registers it up front.
public class use_http_transport_5238
{
    [Fact]
    public async Task publish_to_an_http_uri()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseHttpTransport();
                opts.PublishMessage<HttpMessage1>().To(new Uri("https://receiver.test/_wolverine/invoke"));
            })
            .StartAsync(TestContext.Current.CancellationToken);

        host.GetRuntime().RoutingFor(typeof(HttpMessage1)).Routes.Single().Describe().Endpoint
            .ShouldBe(new Uri("https://receiver.test/_wolverine/invoke"));
    }

    [Fact]
    public async Task listen_for_messages_from_an_http_uri()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseHttpTransport();
                opts.ListenForMessagesFrom(new Uri("http://localhost:5000/_wolverine/batch/control"));
            })
            .StartAsync(TestContext.Current.CancellationToken);

        host.GetRuntime().Options.Transports.GetOrCreate<HttpTransport>()
            .EndpointFor("http://localhost:5000/_wolverine/batch/control").IsListener.ShouldBeTrue();
    }

    [Fact]
    public async Task resolve_an_http_destination_at_runtime()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.UseHttpTransport())
            .StartAsync(TestContext.Current.CancellationToken);

        var runtime = host.GetRuntime();
        runtime.Endpoints.GetOrBuildSendingAgent(new Uri("https://receiver.test/_wolverine/invoke")).Endpoint
            .ShouldBeOfType<HttpEndpoint>();
        Should.NotThrow(() => host.MessageBus().EndpointFor(new Uri("http://receiver.test/_wolverine/invoke")));
    }

    [Fact]
    public void use_http_transport_is_idempotent()
    {
        var options = new WolverineOptions();
        options.UseHttpTransport().ShouldBeSameAs(options.UseHttpTransport());
    }
}

public class invoke_with_an_unregistered_content_type_5238 : IntegrationContext
{
    public invoke_with_an_unregistered_content_type_5238(AppFixture fixture) : base(fixture)
    {
    }

    // Used to throw ArgumentOutOfRangeException out of FindSerializer() and answer 500
    [Fact]
    public async Task invoke_returns_415()
    {
        var envelope = new Envelope
        {
            MessageType = typeof(HttpMessage1).ToMessageTypeName(),
            Data = [1, 2, 3],
            ContentType = "application/x-not-registered"
        };

        await Scenario(s =>
        {
            s.Post.ByteArray(EnvelopeSerializer.Serialize(envelope)).ToUrl("/_wolverine/invoke")
                .ContentType(HttpTransport.EnvelopeContentType);
            s.StatusCodeShouldBe(415);
        });
    }
}
