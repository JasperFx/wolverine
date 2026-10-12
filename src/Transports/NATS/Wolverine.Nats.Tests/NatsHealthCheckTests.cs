using IntegrationTests;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Nats.Internal;
using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A listener the agent framework starts after the host -- an exclusive listener, a shard -- can fail to start
/// without failing the host: the agent logs it, and the listener stays down. The NATS health check still reported
/// Healthy, since all it looked at was the connection.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsHealthCheckTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsHealthCheckTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task a_listener_that_fails_to_start_is_unhealthy_until_it_starts()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var id = Guid.NewGuid().ToString("N")[..8];
        var stream = $"HEALTH_{id}";
        var subject = $"health.{id}";

        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        var js = connection.CreateJetStreamContext();
        await js.CreateStreamAsync(new StreamConfig(stream, [subject]), cancellation);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "HealthCheck";
                opts.UseNats(_fixture.ConnectionString);
                opts.ListenToNatsSubject(subject).UseJetStream(stream, $"health-{id}");
            })
            .StartAsync(cancellation);

        var runtime = host.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<NatsTransport>();
        var healthCheck = transport.BuildHealthCheck(runtime);
        var endpoint = transport.EndpointForSubject(subject);

        (await healthCheck.CheckHealthAsync(cancellation)).Status.ShouldBe(TransportHealthStatus.Healthy);

        // A later start, as when the agent framework hands an exclusive listener to this node, that cannot create
        // its consumer
        await runtime.Endpoints.StopListenerAsync(endpoint, cancellation);
        await js.DeleteStreamAsync(stream, cancellation);
        await Should.ThrowAsync<Exception>(() => runtime.Endpoints.StartListenerAsync(endpoint, cancellation));

        var failed = await healthCheck.CheckHealthAsync(cancellation);
        _output.WriteLine(failed.Message ?? "(no message)");
        failed.Status.ShouldBe(TransportHealthStatus.Unhealthy);
        failed.Message.ShouldNotBeNull().ShouldContain(endpoint.Uri.ToString());

        // The next start that succeeds clears it
        await js.CreateStreamAsync(new StreamConfig(stream, [subject]), cancellation);
        await runtime.Endpoints.StartListenerAsync(endpoint, cancellation);

        (await healthCheck.CheckHealthAsync(cancellation)).Status.ShouldBe(TransportHealthStatus.Healthy);
    }
}
