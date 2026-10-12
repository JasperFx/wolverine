using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using NATS.Net;
using Shouldly;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A JetStream listener without a consumer name gets a consumer Wolverine creates with a filter but no name. NATS.Net
/// sends that request to <c>$JS.API.CONSUMER.CREATE.{stream}.{filter}</c> (nats-io/nats.net#448), and the server reads
/// the token after the stream as the consumer's name: a single-token subject quietly named the consumer after itself,
/// and a subject with a dot failed every start with 10131 ("Consumer create request did not match filtered subject
/// from create subject"). That included the shard listeners of a <c>UseShardedNatsSubjects()</c> base name with a dot.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsUnnamedConsumerTests
{
    private readonly NatsContainerFixture _fixture;
    private readonly ITestOutputHelper _output;

    public NatsUnnamedConsumerTests(NatsContainerFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task an_unnamed_listener_on_a_subject_with_a_dot_starts_and_receives()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var stream = $"UNNAMED_{id}";
        var subject = $"unnamed.{id}.orders";
        await createStreamAsync(stream, $"unnamed.{id}.>");

        using var host = await startAsync(subject, stream);

        var session = await host.TrackActivity()
            .IncludeExternalTransports()
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new OrderPlaced(id));

        session.Received.SingleMessage<OrderPlaced>().OrderId.ShouldBe(id);
    }

    [Fact]
    public async Task an_unnamed_listener_on_a_single_token_subject_keeps_its_consumer_name()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var stream = $"UNNAMEDFLAT_{id}";
        var subject = $"unnamedflat{id}";
        await createStreamAsync(stream, subject);

        using var host = await startAsync(subject, stream);

        // What the server named it before, so an upgrade finds the same consumer
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        var consumer = await connection.CreateJetStreamContext()
            .GetConsumerAsync(stream, subject, TestContext.Current.CancellationToken);
        consumer.Info.Config.FilterSubject.ShouldBe(subject);
    }

    private Task<IHost> startAsync(string subject, string stream)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "UnnamedConsumer";
                opts.UseNats(_fixture.ConnectionString);
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream);
                opts.ListenToNatsSubject(subject).UseJetStream(stream);
            })
            .StartAsync(TestContext.Current.CancellationToken);
    }

    private async Task createStreamAsync(string stream, string subjects)
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = _fixture.ConnectionString });
        await connection.CreateJetStreamContext()
            .CreateStreamAsync(new StreamConfig(stream, [subjects]), TestContext.Current.CancellationToken);
    }
}
