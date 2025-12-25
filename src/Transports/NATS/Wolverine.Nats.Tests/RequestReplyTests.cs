using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using Wolverine.Nats.Tests.Helpers;
using Wolverine.Runtime.Routing;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

[Collection("NATS Integration Tests")]
[Trait("Category", "Integration")]
public class RequestReplyTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private IHost? _host;

    public RequestReplyTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public async Task InitializeAsync()
    {
        // Use NATS_URL environment variable or default to local Docker port
        var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
        
        // Skip tests if NATS server is not available
        if (!await IsNatsServerAvailable(natsUrl))
        {
            _output.WriteLine($"NATS server not available at {natsUrl}. Skipping integration tests.");
            return;
        }

        _host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(logging => logging.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "RequestReplyTest";
                opts.UseNats(natsUrl);

                // Configure publishing
                opts.PublishMessage<PingMessage>().ToNatsSubject("ping.request");

                // Configure listening
                opts.ListenToNatsSubject("ping.request");
            })
            .StartAsync();
    }

    private async Task<bool> IsNatsServerAvailable(string natsUrl)
    {
        try
        {
            await using var connection = new NatsConnection(NatsOpts.Default with { Url = natsUrl });
            await connection.ConnectAsync();
            return connection.ConnectionState == NatsConnectionState.Open;
        }
        catch
        {
            return false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    [Fact]
    public async Task can_send_request_and_receive_reply()
    {
        // Skip if NATS server not available or host not initialized
        if (_host == null)
        {
            _output.WriteLine("Skipping test - NATS server not available");
            return;
        }

        var (session, response) = await _host
            .TrackActivity()
            .Timeout(TimeSpan.FromSeconds(10))
            .InvokeAndWaitAsync<PongMessage>(new PingMessage { Name = "TestPing" });

        Assert.NotNull(response);
        Assert.Equal("Hello TestPing", response.Message);
    }


    [Fact]
    public async Task throws_unknown_endpoint_exception_for_invalid_endpoint()
    {
        // Skip if NATS server not available or host not initialized
        if (_host == null)
        {
            _output.WriteLine("Skipping test - NATS server not available");
            return;
        }

        var bus = _host.Services.GetRequiredService<IMessageBus>();

        await Assert.ThrowsAsync<UnknownEndpointException>(async () =>
        {
            await bus.EndpointFor("nats://nonexistent.subject")
                .InvokeAsync<PongMessage>(
                    new PingMessage { Name = "NoEndpoint" },
                    timeout: TimeSpan.FromSeconds(1)
                );
        });
    }
}

public class PingMessage
{
    public string Name { get; set; } = "";
}

public class PongMessage
{
    public string Message { get; set; } = "";
}

public class PingHandler
{
    public PongMessage Handle(PingMessage ping)
    {
        return new PongMessage { Message = $"Hello {ping.Name}" };
    }
}
