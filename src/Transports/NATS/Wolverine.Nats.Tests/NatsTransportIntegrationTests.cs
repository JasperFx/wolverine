using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using JasperFx.Core;
using Wolverine.Nats.Internal;
using Wolverine.Nats.Tests.Helpers;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;
using FluentAssertions;
using NATS.Client.Core;

namespace Wolverine.Nats.Tests;

[Collection("NATS Integration Tests")]
[Trait("Category", "Integration")]
public class NatsTransportIntegrationTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _sender;
    private IHost? _receiver;
    private static int _counter = 0;
    private string _receiverSubject = "";

    public NatsTransportIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL");

        if (string.IsNullOrEmpty(natsUrl))
        {
            if (await IsNatsAvailable("nats://localhost:4222"))
            {
                natsUrl = "nats://localhost:4222";
            }
            return;
            
        }

        if (!await IsNatsAvailable(natsUrl))
        {
            return;
        }

        var number = ++_counter;
        _receiverSubject = $"test.receiver.{number}";

        _sender = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Sender";
                opts.UseNats(natsUrl).AutoProvision();
                opts.PublishAllMessages().ToNatsSubject(_receiverSubject);
            })
            .StartAsync();

        _receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Receiver";
                opts.UseNats(natsUrl).AutoProvision();
                opts.ListenToNatsSubject(_receiverSubject).Named("receiver");
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_sender != null)
            await _sender.StopAsync();
        if (_receiver != null)
            await _receiver.StopAsync();
        _sender?.Dispose();
        _receiver?.Dispose();
    }

    [Fact]
    public async Task send_message_to_and_receive_through_nats()
    {
        if (_sender == null || _receiver == null)
            return; // Skip if NATS not available

        var message = new TestMessage(Guid.NewGuid(), "Hello NATS!");

        var tracked = await _sender
            .TrackActivity()
            .AlsoTrack(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(message);

        tracked.Sent.SingleMessage<TestMessage>().Should().BeEquivalentTo(message);

        tracked.Received.SingleMessage<TestMessage>().Should().BeEquivalentTo(message);
    }

    [Fact]
    public async Task can_send_and_receive_multiple_messages()
    {
        if (_sender == null || _receiver == null)
            return;

        var messages = new[]
        {
            new TestMessage(Guid.NewGuid(), "Message 1"),
            new TestMessage(Guid.NewGuid(), "Message 2"),
            new TestMessage(Guid.NewGuid(), "Message 3")
        };

        foreach (var message in messages)
        {
            var tracked = await _sender
                .TrackActivity()
                .AlsoTrack(_receiver)
                .Timeout(30.Seconds())
                .SendMessageAndWaitAsync(message);

            tracked.Sent.SingleMessage<TestMessage>().Should().BeEquivalentTo(message);

            tracked.Received.SingleMessage<TestMessage>().Should().BeEquivalentTo(message);
        }
    }

    [Fact]
    public void nats_transport_is_registered()
    {
        if (_sender == null)
            return;

        var runtime = _sender.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();

        transport.Should().NotBeNull();
        transport.Protocol.Should().Be("nats");
    }

    [Fact]
    public void endpoints_are_configured_correctly()
    {
        if (_receiver == null)
            return;

        var runtime = _receiver.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();

        var endpointUri = new Uri($"nats://subject/{_receiverSubject}");
        var endpoint = transport.TryGetEndpoint(endpointUri);

        endpoint.Should().NotBeNull();
        endpoint.Should().BeOfType<NatsEndpoint>();

        var natsEndpoint = (NatsEndpoint)endpoint!;
        natsEndpoint.Subject.Should().Be(_receiverSubject);
        natsEndpoint.EndpointName.Should().Be("receiver");
    }

    [Fact]
    public void server_version_is_detected_and_scheduled_send_support_is_determined()
    {
        if (_sender == null)
            return;

        var runtime = _sender.Services.GetRequiredService<IWolverineRuntime>();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();

        transport.Connection.ServerInfo.Should().NotBeNull();
        transport.Connection.ServerInfo!.Version.Should().NotBeNullOrEmpty();
        
        _output.WriteLine($"NATS Server Version: {transport.Connection.ServerInfo.Version}");
        _output.WriteLine($"ServerSupportsScheduledSend: {transport.ServerSupportsScheduledSend}");

        var versionString = transport.Connection.ServerInfo.Version.Split('-')[0];
        if (Version.TryParse(versionString, out var serverVersion))
        {
            var minVersion = new Version(2, 12, 0);
            var expectedSupport = serverVersion >= minVersion;
            
            transport.ServerSupportsScheduledSend.Should().Be(expectedSupport,
                $"Server version {serverVersion} should {(expectedSupport ? "" : "not ")}support scheduled send (min: {minVersion})");
        }
    }

    [Fact]
    public async Task receive_message_without_type_header_using_default_incoming_message()
    {
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        if (!await IsNatsAvailable(natsUrl)) return;

        var subject = Guid.NewGuid().ToString();

        using var receiver = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "ReceiverWithDefault";
                opts.UseNats(natsUrl).AutoProvision();
                opts.ListenToNatsSubject(subject)
                    .DefaultIncomingMessage<DefaultTestMessage>()
                    .BufferedInMemory();
            })
            .StartAsync();

        await using var nats = new NatsConnection(new NatsOpts { Url = natsUrl });
        await nats.ConnectAsync();

        var messageData = System.Text.Encoding.UTF8.GetBytes("{\"Text\":\"Hello without header\"}");

        var tracked = await receiver.TrackActivity()
            .Timeout(10.Seconds())
            .WaitForMessageToBeReceivedAt<DefaultTestMessage>(receiver)
            .ExecuteAndWaitAsync(c =>
            {
                return nats.PublishAsync(subject, messageData).AsTask();
            });

        tracked.Received.SingleMessage<DefaultTestMessage>().Text.Should().Be("Hello without header");
    }

    private async Task<bool> IsNatsAvailable(string natsUrl)
    {
        try
        {
            using var testHost = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UseNats(natsUrl);
                })
                .StartAsync();

            await testHost.StopAsync();
            return true;
        }
        catch
        {
            return false;
        }
    }
}

public record TestMessage(Guid Id, string Text);

public class TestMessageHandler
{
    public void Handle(TestMessage message)
    {
    }
}

public record DefaultTestMessage(string Text);

public class DefaultTestMessageHandler
{
    public void Handle(DefaultTestMessage message)
    {
    }
}
