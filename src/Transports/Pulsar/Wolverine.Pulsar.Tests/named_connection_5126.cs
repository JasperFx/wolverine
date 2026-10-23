using DotPulsar.Extensions;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pulsar.Tests;

/// <summary>
/// GH-5126: <c>UsePulsarUsingNamedConnection</c> / <c>AddNamedPulsarBrokerUsingNamedConnection</c> read the
/// Pulsar service URL from <c>ConnectionStrings:{name}</c> once the container is built.
/// </summary>
public class named_connection_5126
{
    private static readonly BrokerName theSecondary = new("secondary");

    private static async Task<Uri> serviceUrlOf(PulsarTransport transport)
    {
        // Building the DotPulsar client does not connect, so this reads back what the builder was given
        await using var client = transport.Builder.Build();
        return client.ServiceUrl;
    }

    [Fact]
    public async Task missing_named_connection_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.UsePulsarUsingNamedConnection("pulsar");
                    opts.StubAllExternalTransports();
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["pulsar"]);
        ex.Message.ShouldContain("'pulsar'");
        ex.Message.ShouldContain("Pulsar");
    }

    [Fact]
    public async Task missing_named_connection_for_named_broker_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await Host.CreateDefaultBuilder()
                .UseWolverine(opts =>
                {
                    opts.AddNamedPulsarBrokerUsingNamedConnection(theSecondary, "pulsar2");
                    opts.StubAllExternalTransports();
                }).StartAsync(TestContext.Current.CancellationToken);
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["pulsar2"]);
    }

    [Theory]
    [InlineData("pulsar://pulsar-host:6650", "pulsar://pulsar-host:6650")]
    [InlineData("pulsar+ssl://pulsar-host:6651", "pulsar+ssl://pulsar-host:6651")]
    [InlineData("pulsar-host:6650", "pulsar://pulsar-host:6650")]
    public async Task applies_the_named_connection_to_the_default_broker(string value, string expected)
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:pulsar"] = value
            }))
            .UseWolverine(opts =>
            {
                opts.UsePulsarUsingNamedConnection("pulsar");
                opts.StubAllExternalTransports();
            }).StartAsync(TestContext.Current.CancellationToken);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PulsarTransport>();
        (await serviceUrlOf(transport)).ShouldBe(new Uri(expected));
    }

    [Fact]
    public async Task applies_the_named_connection_to_a_named_broker()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:pulsar"] = "pulsar://primary:6650",
                ["ConnectionStrings:pulsar2"] = "pulsar://secondary-host:6660"
            }))
            .UseWolverine(opts =>
            {
                opts.UsePulsarUsingNamedConnection("pulsar");
                opts.AddNamedPulsarBrokerUsingNamedConnection(theSecondary, "pulsar2");
                opts.StubAllExternalTransports();
            }).StartAsync(TestContext.Current.CancellationToken);

        var transports = host.GetRuntime().Options.Transports;
        (await serviceUrlOf(transports.GetOrCreate<PulsarTransport>())).ShouldBe(new Uri("pulsar://primary:6650"));
        (await serviceUrlOf(transports.GetOrCreate<PulsarTransport>(theSecondary)))
            .ShouldBe(new Uri("pulsar://secondary-host:6660"));
    }

    [Fact]
    public async Task extra_configuration_is_applied_after_the_named_connection()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:pulsar"] = "pulsar://from-config:6650"
            }))
            .UseWolverine(opts =>
            {
                opts.UsePulsarUsingNamedConnection("pulsar", b => b.ServiceUrl(new Uri("pulsar://overridden:6650")));
                opts.StubAllExternalTransports();
            }).StartAsync(TestContext.Current.CancellationToken);

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PulsarTransport>();
        (await serviceUrlOf(transport)).ShouldBe(new Uri("pulsar://overridden:6650"));
    }
}

[Collection("pulsar")]
public class named_connection_5126_end_to_end
{
    [Fact]
    public async Task round_trips_a_message_through_a_named_connection()
    {
        var topic = $"persistent://public/default/named-connection-{Guid.NewGuid():N}";
        var secondaryTopic = $"persistent://public/default/named-connection-2-{Guid.NewGuid():N}";
        var secondary = new BrokerName("secondary");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // What the AppHost would supply through WithReference(...)
                ["ConnectionStrings:pulsar"] = PulsarContainerFixture.ServiceUrl.ToString(),
                ["ConnectionStrings:pulsar2"] = PulsarContainerFixture.ServiceUrl.ToString()
            }))
            .UseWolverine(opts =>
            {
                #region sample_pulsar_using_named_connection

                // Reads the Pulsar service URL (e.g. "pulsar://localhost:6650") from
                // the "pulsar" connection string in IConfiguration
                opts.UsePulsarUsingNamedConnection("pulsar");

                #endregion

                #region sample_pulsar_named_broker_using_named_connection

                // A second Pulsar broker, whose service URL comes from the "pulsar2" connection string
                opts.AddNamedPulsarBrokerUsingNamedConnection(new BrokerName("secondary"), "pulsar2");

                #endregion

                opts.PublishMessage<NamedConnectionPulsarMessage>().ToPulsarTopic(topic);
                opts.ListenToPulsarTopic(topic)
                    .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                    .BeginAtEarliest();

                opts.PublishMessage<NamedConnectionPulsarMessage2>().ToPulsarTopicOnNamedBroker(secondary, secondaryTopic);
                opts.ListenToPulsarTopicOnNamedBroker(secondary, secondaryTopic)
                    .SubscriptionName("sub-" + Guid.NewGuid().ToString("N"))
                    .BeginAtEarliest();

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NamedConnectionPulsarHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<NamedConnectionPulsarMessage>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedConnectionPulsarMessage("default")));

        session.Received.SingleMessage<NamedConnectionPulsarMessage>().Id.ShouldBe("default");

        var session2 = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<NamedConnectionPulsarMessage2>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedConnectionPulsarMessage2("named")));

        var received = session2.Received.SingleEnvelope<NamedConnectionPulsarMessage2>();
        received.Message.ShouldBeOfType<NamedConnectionPulsarMessage2>().Id.ShouldBe("named");
        received.Destination!.Scheme.ShouldBe("secondary");
    }
}

public record NamedConnectionPulsarMessage(string Id);

public record NamedConnectionPulsarMessage2(string Id);

public class NamedConnectionPulsarHandler
{
    public void Handle(NamedConnectionPulsarMessage message)
    {
    }

    public void Handle(NamedConnectionPulsarMessage2 message)
    {
    }
}
