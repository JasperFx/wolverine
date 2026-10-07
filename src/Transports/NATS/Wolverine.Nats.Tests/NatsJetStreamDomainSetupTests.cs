using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Shouldly;
using Wolverine.Nats.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// <c>NatsEndpoint.SetupAsync</c> -- what <c>resources setup</c> and <c>AddResourceSetupOnStartup()</c> run -- has to
/// look up and create streams, and update named consumers, in the JetStream domain the transport is configured
/// for, like every other JetStream call the transport makes. The client here sits on a leaf node with its own
/// JetStream; the application's streams belong to the hub.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsJetStreamDomainSetupTests : IClassFixture<NatsJetStreamDomainFixture>
{
    private readonly NatsJetStreamDomainFixture _domains;
    private readonly ITestOutputHelper _output;

    public NatsJetStreamDomainSetupTests(NatsJetStreamDomainFixture domains, ITestOutputHelper output)
    {
        _domains = domains;
        _output = output;
    }

    [Fact]
    public async Task setup_creates_stream_in_configured_domain()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"DOMAIN_SETUP_{suffix}";
        var subject = $"domain.setup.{suffix}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DomainSetup";
                opts.UseNats(_domains.LeafConnectionString).UseJetStreamDomain(NatsJetStreamDomainFixture.HubDomain);

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream);

                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        await using var connection = await connectToTheLeafAsync();

        var inHub = await findStreamAsync(NatsJetStreamDomainFixture.JetStreamFor(connection, NatsJetStreamDomainFixture.HubDomain), stream);
        var inLeaf = await findStreamAsync(NatsJetStreamDomainFixture.JetStreamFor(connection, NatsJetStreamDomainFixture.LeafDomain), stream);

        inLeaf.ShouldBeNull();
        inHub.ShouldNotBeNull().Info.Config.Subjects.ShouldBe([subject]);
    }

    [Fact]
    public async Task setup_updates_named_consumer_in_configured_domain()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"DOMAIN_CONSUMER_{suffix}";
        var subject = $"domain.consumer.{suffix}";
        var consumer = $"domain-consumer-{suffix}";

        await using var connection = await connectToTheLeafAsync();
        var hub = NatsJetStreamDomainFixture.JetStreamFor(connection, NatsJetStreamDomainFixture.HubDomain);

        // The stream and the named consumer already exist in the hub, provisioned earlier with MaxDeliver 1
        await hub.CreateStreamAsync(new StreamConfig(stream, [subject]), TestContext.Current.CancellationToken);
        await hub.CreateOrUpdateConsumerAsync(stream, new ConsumerConfig
        {
            Name = consumer,
            DurableName = consumer,
            FilterSubject = subject,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            MaxDeliver = 1
        }, TestContext.Current.CancellationToken);

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DomainConsumer";
                opts.UseNats(_domains.LeafConnectionString)
                    .UseJetStreamDomain(NatsJetStreamDomainFixture.HubDomain)
                    // The listener would reconcile the consumer itself; leave the update to resource setup
                    .Provisioning(NatsProvisioning.CreateOnly);

                opts.Policies.DisableConventionalLocalRouting();
                opts.ListenToNatsSubject(subject)
                    .UseJetStream(stream, consumer)
                    .ConfigureDeadLetterQueue(7);

                opts.Services.AddResourceSetupOnStartup();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var info = await hub.GetConsumerAsync(stream, consumer, TestContext.Current.CancellationToken);
        info.Info.Config.MaxDeliver.ShouldBe(7);

        (await findStreamAsync(NatsJetStreamDomainFixture.JetStreamFor(connection, NatsJetStreamDomainFixture.LeafDomain), stream))
            .ShouldBeNull();
    }

    [Fact]
    public async Task setup_does_not_create_a_stream_when_the_lookup_fails_for_another_reason()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var stream = $"DOMAIN_NOWHERE_{suffix}";
        var subject = $"domain.nowhere.{suffix}";

        // No JetStream answers for this domain, so the stream lookup fails without a 404
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "DomainNowhere";
                opts.UseNats(_domains.LeafConnectionString).UseJetStreamDomain("nowhere");

                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).UseJetStream(stream);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var endpoint = host.GetRuntime().Options.Transports.GetOrCreate<NatsTransport>().EndpointForSubject(subject);
        var logger = new RecordingLogger();

        await Should.ThrowAsync<NatsNoRespondersException>(() => endpoint.SetupAsync(logger).AsTask());

        logger.Messages.ShouldNotContain(m => m.Contains("Creating JetStream stream"));
    }

    private async Task<NatsConnection> connectToTheLeafAsync()
    {
        var connection = new NatsConnection(new NatsOpts { Url = _domains.LeafConnectionString });
        await connection.ConnectAsync();
        return connection;
    }

    private static async Task<INatsJSStream?> findStreamAsync(INatsJSContext js, string stream)
    {
        try
        {
            return await js.GetStreamAsync(stream);
        }
        catch (NatsJSApiException e) when (e.Error.Code == 404)
        {
            return null;
        }
    }

    private class RecordingLogger : ILogger
    {
        public List<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (Messages)
            {
                Messages.Add(formatter(state, exception));
            }
        }
    }
}
