using System.Text.Json;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Http.Transport;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class HttpTransportConfigurationTests
{
    [Fact]
    public async Task to_http_endpoint_creates_endpoint_with_correct_uri()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages()
                    .ToHttpEndpoint("https://external-service.com/api");
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<HttpTransport>();
        
        var endpoint = transport.EndpointFor("https://external-service.com/api");
        endpoint.ShouldNotBeNull();
        endpoint.OutboundUri.ShouldBe("https://external-service.com/api");
    }

    [Fact]
    public async Task to_http_endpoint_with_native_scheduled_send_sets_flag()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages()
                    .ToHttpEndpoint("https://scheduler.com/api", supportsNativeScheduledSend: true);
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<HttpTransport>();
        
        var endpoint = transport.EndpointFor("https://scheduler.com/api");
        endpoint.SupportsNativeScheduledSend.ShouldBeTrue();
    }

    [Fact]
    public async Task to_http_endpoint_without_native_scheduled_send_defaults_to_false()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages()
                    .ToHttpEndpoint("https://regular.com/api");
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<HttpTransport>();
        
        var endpoint = transport.EndpointFor("https://regular.com/api");
        endpoint.SupportsNativeScheduledSend.ShouldBeFalse();
    }

    [Fact]
    public async Task to_http_endpoint_with_cloud_events_sets_serializer_options()
    {
        var customOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseUpper,
            WriteIndented = true
        };

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages()
                    .ToHttpEndpoint("https://cloudevents.com/api", 
                        useCloudEvents: true, 
                        options: customOptions);
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<HttpTransport>();
        
        var endpoint = transport.EndpointFor("https://cloudevents.com/api");
        endpoint.SerializerOptions.ShouldBeSameAs(customOptions);
        endpoint.SerializerOptions.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.SnakeCaseUpper);
        endpoint.SerializerOptions.WriteIndented.ShouldBeTrue();
    }

    [Fact]
    public async Task to_http_endpoint_without_cloud_events_keeps_default_options()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages()
                    .ToHttpEndpoint("https://binary.com/api");
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<HttpTransport>();
        
        var endpoint = transport.EndpointFor("https://binary.com/api");
        // Default options should have CamelCase naming
        endpoint.SerializerOptions.PropertyNamingPolicy.ShouldBe(JsonNamingPolicy.CamelCase);
        endpoint.SerializerOptions.WriteIndented.ShouldBeFalse();
    }

    [Fact]
    public async Task to_http_endpoint_returns_subscriber_configuration()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var config = opts.PublishAllMessages()
                    .ToHttpEndpoint("https://test.com/api");

                config.ShouldBeOfType<HttpTransportSubscriberConfiguration>();
            })
            .StartAsync();
    }

    [Fact]
    public async Task can_chain_subscriber_configuration_methods()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PublishAllMessages()
                    .ToHttpEndpoint("https://test.com/api")
                    .SendInline()
                    .CustomizeOutgoing(e => e.CorrelationId = "test-correlation");
            })
            .StartAsync();

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<HttpTransport>();
        
        var endpoint = transport.EndpointFor("https://test.com/api");
        endpoint.Mode.ShouldBe(Wolverine.Configuration.EndpointMode.Inline);
    }

    [Fact]
    public void can_configure_multiple_http_endpoints_with_different_settings()
    {
        var transport = new HttpTransport();
        
        var endpoint1 = transport.EndpointFor("https://service1.com/api");
        endpoint1.SupportsNativeScheduledSend = false;

        var endpoint2 = transport.EndpointFor("https://service2.com/api");
        endpoint2.SupportsNativeScheduledSend = true;

        endpoint1.SupportsNativeScheduledSend.ShouldBeFalse();
        endpoint2.SupportsNativeScheduledSend.ShouldBeTrue();
        
        // Verify endpoints are cached and different
        transport.EndpointFor("https://service1.com/api").ShouldBeSameAs(endpoint1);
        transport.EndpointFor("https://service2.com/api").ShouldBeSameAs(endpoint2);
    }
}

public record TestMessage1(string Value);
public record TestMessage2(string Value);

