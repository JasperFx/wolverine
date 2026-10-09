using System.Diagnostics;
using JasperFx.Core;
using Microsoft.Extensions.Logging.Abstractions;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Testcontainers.Nats;
using Wolverine.Nats.Internal;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// With every server gone, a JetStream publish in NATS.Net waits for its PubAck far beyond the request timeout --
/// minutes -- unless its cancellation token ends it. Wolverine only passed the host's lifetime token, so a sending
/// agent (or a dead-letter forward) hung for as long as the outage lasted. Each publish now has its own deadline,
/// the connection's request timeout.
/// </summary>
[Trait("Category", "Integration")]
public class NatsPublishDeadlineTests : IAsyncLifetime
{
    private NatsContainer? _server;

    public async ValueTask InitializeAsync()
    {
        _server = new NatsBuilder().WithImage(NatsContainerFixture.NatsImage).Build();
        await _server.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (_server != null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task a_jetstream_publish_gives_up_when_every_server_is_gone()
    {
        var subject = $"deadline.{Guid.NewGuid():N}";
        await using var connection = new NatsConnection(new NatsOpts
        {
            Url = _server!.GetConnectionString(),
            CommandTimeout = 2.Seconds(),
            RequestTimeout = 2.Seconds()
        });
        await connection.ConnectAsync();
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig($"DEADLINE_{Guid.NewGuid():N}", [subject]),
            TestContext.Current.CancellationToken);

        var publisher = new JetStreamPublisher(connection, js, NullLogger<NatsEndpoint>.Instance);
        await publisher.PublishAsync(subject, [1], new NatsHeaders(), null, new Envelope(),
            TestContext.Current.CancellationToken);

        await _server.StopAsync(TestContext.Current.CancellationToken);

        var stopwatch = Stopwatch.StartNew();
        var publish = publisher.PublishAsync(subject, [2], new NatsHeaders(), null, new Envelope(),
            TestContext.Current.CancellationToken).AsTask();

        // Generous: the point is that it ends at all
        var finished = await Task.WhenAny(publish, Task.Delay(30.Seconds(), TestContext.Current.CancellationToken));
        stopwatch.Stop();

        finished.ShouldBeSameAs(publish, $"the publish was still waiting after {stopwatch.Elapsed}");
        publish.Exception.ShouldNotBeNull().InnerException.ShouldBeOfType<TimeoutException>();
    }
}
