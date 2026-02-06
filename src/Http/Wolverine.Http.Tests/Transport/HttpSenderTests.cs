using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Http.Transport;
using Wolverine.Runtime;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.Http.Tests.Transport;

public class HttpSenderTests
{
    [Fact]
    public async Task inline_sender_supports_native_scheduled_send_when_endpoint_configured()
    {
        var endpoint = new HttpEndpoint(new Uri("https://scheduler.com/api"), EndpointRole.Application)
        {
            SupportsNativeScheduledSend = true,
            OutboundUri = "https://scheduler.com/api"
        };

        var services = new ServiceCollection();
        var mockClient = Substitute.For<IWolverineHttpTransportClient>();
        services.AddSingleton(mockClient);
        var serviceProvider = services.BuildServiceProvider();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Services.Returns(serviceProvider);

        var sender = new InlineHttpSender(endpoint, runtime, serviceProvider);

        sender.SupportsNativeScheduledSend.ShouldBeTrue();
    }

    [Fact]
    public async Task inline_sender_does_not_support_native_scheduled_send_by_default()
    {
        var endpoint = new HttpEndpoint(new Uri("https://regular.com/api"), EndpointRole.Application)
        {
            OutboundUri = "https://regular.com/api"
        };

        var services = new ServiceCollection();
        var mockClient = Substitute.For<IWolverineHttpTransportClient>();
        services.AddSingleton(mockClient);
        var serviceProvider = services.BuildServiceProvider();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Services.Returns(serviceProvider);

        var sender = new InlineHttpSender(endpoint, runtime, serviceProvider);

        sender.SupportsNativeScheduledSend.ShouldBeFalse();
    }

    [Fact]
    public async Task inline_sender_uses_wolverine_http_transport_client()
    {
        var endpoint = new HttpEndpoint(new Uri("https://test.com/api"), EndpointRole.Application)
        {
            OutboundUri = "https://test.com/api"
        };

        var services = new ServiceCollection();
        var mockClient = Substitute.For<IWolverineHttpTransportClient>();
        services.AddSingleton(mockClient);
        var serviceProvider = services.BuildServiceProvider();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Services.Returns(serviceProvider);

        var sender = new InlineHttpSender(endpoint, runtime, serviceProvider);

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            Data = new byte[] { 1, 2, 3 }
        };

        await sender.SendAsync(envelope);

        await mockClient.Received(1).SendAsync(
            Arg.Is("https://test.com/api"),
            Arg.Is<Envelope>(e => e.Id == envelope.Id),
            Arg.Any<System.Text.Json.JsonSerializerOptions>());
    }

    [Fact]
    public async Task inline_sender_handles_exceptions_gracefully()
    {
        var endpoint = new HttpEndpoint(new Uri("https://failing.com/api"), EndpointRole.Application)
        {
            OutboundUri = "https://failing.com/api"
        };

        var services = new ServiceCollection();
        var mockClient = Substitute.For<IWolverineHttpTransportClient>();
        mockClient.SendAsync(Arg.Any<string>(), Arg.Any<Envelope>(), Arg.Any<System.Text.Json.JsonSerializerOptions>())
            .Returns(Task.FromException(new Exception("Network error")));
        services.AddSingleton(mockClient);
        var serviceProvider = services.BuildServiceProvider();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Services.Returns(serviceProvider);

        var sender = new InlineHttpSender(endpoint, runtime, serviceProvider);

        var envelope = new Envelope
        {
            Id = Guid.NewGuid(),
            Data = new byte[] { 1, 2, 3 }
        };

        // Should not throw - errors are logged
        await sender.SendAsync(envelope);
    }

    [Fact]
    public async Task inline_sender_logs_error_if_client_not_registered()
    {
        var endpoint = new HttpEndpoint(new Uri("https://test.com/api"), EndpointRole.Application)
        {
            OutboundUri = "https://test.com/api"
        };

        var services = new ServiceCollection();
        // Not registering IWolverineHttpTransportClient
        var serviceProvider = services.BuildServiceProvider();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Services.Returns(serviceProvider);

        var sender = new InlineHttpSender(endpoint, runtime, serviceProvider);

        var envelope = new Envelope { Id = Guid.NewGuid() };

        // InlineHttpSender catches exceptions and logs them instead of throwing
        // This should not throw - it logs the error
        await sender.SendAsync(envelope);
        
        // Test passes if no exception is thrown
        true.ShouldBeTrue();
    }

    [Fact]
    public async Task batched_sender_protocol_sends_batch_via_client()
    {
        var endpoint = new HttpEndpoint(new Uri("https://batch.com/api"), EndpointRole.Application)
        {
            OutboundUri = "https://batch.com/api"
        };

        var services = new ServiceCollection();
        var mockClient = Substitute.For<IWolverineHttpTransportClient>();
        services.AddSingleton(mockClient);
        var serviceProvider = services.BuildServiceProvider();

        var protocol = new HttpSenderProtocol(endpoint, serviceProvider);

        var envelopes = new[]
        {
            new Envelope { Id = Guid.NewGuid(), Data = new byte[] { 1, 2, 3 } },
            new Envelope { Id = Guid.NewGuid(), Data = new byte[] { 4, 5, 6 } }
        };
        var batch = new OutgoingMessageBatch(endpoint.Uri, envelopes);

        var callback = Substitute.For<ISenderCallback>();

        await protocol.SendBatchAsync(callback, batch);

        await mockClient.Received(1).SendBatchAsync(
            Arg.Is("https://batch.com/api"),
            Arg.Is<OutgoingMessageBatch>(b => b.Messages.Count == 2));
    }

    [Fact]
    public async Task batched_sender_protocol_throws_if_client_not_registered()
    {
        var endpoint = new HttpEndpoint(new Uri("https://batch.com/api"), EndpointRole.Application)
        {
            OutboundUri = "https://batch.com/api"
        };

        var services = new ServiceCollection();
        // Not registering IWolverineHttpTransportClient
        var serviceProvider = services.BuildServiceProvider();

        var protocol = new HttpSenderProtocol(endpoint, serviceProvider);

        var batch = new OutgoingMessageBatch(endpoint.Uri, new[] { new Envelope { Data = new byte[] { 1 } } });
        var callback = Substitute.For<ISenderCallback>();

        // GetRequiredService throws InvalidOperationException when service is not found
        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            await protocol.SendBatchAsync(callback, batch);
        });
        
        ex.Message.ShouldContain("IWolverineHttpTransportClient");
    }

    [Fact]
    public async Task inline_sender_ping_returns_true()
    {
        var endpoint = new HttpEndpoint(new Uri("https://test.com/api"), EndpointRole.Application);
        var services = new ServiceCollection();
        var mockClient = Substitute.For<IWolverineHttpTransportClient>();
        services.AddSingleton(mockClient);
        var serviceProvider = services.BuildServiceProvider();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Services.Returns(serviceProvider);

        var sender = new InlineHttpSender(endpoint, runtime, serviceProvider);

        var result = await sender.PingAsync();
        result.ShouldBeTrue();
    }

    [Fact]
    public void inline_sender_destination_matches_endpoint_uri()
    {
        var uri = new Uri("https://test.com/api");
        var endpoint = new HttpEndpoint(uri, EndpointRole.Application);
        
        var services = new ServiceCollection();
        var mockClient = Substitute.For<IWolverineHttpTransportClient>();
        services.AddSingleton(mockClient);
        var serviceProvider = services.BuildServiceProvider();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.LoggerFactory.Returns(NullLoggerFactory.Instance);
        runtime.Services.Returns(serviceProvider);

        var sender = new InlineHttpSender(endpoint, runtime, serviceProvider);

        sender.Destination.ShouldBe(uri);
    }
}

