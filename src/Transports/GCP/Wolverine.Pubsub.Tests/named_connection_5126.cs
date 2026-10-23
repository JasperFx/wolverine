using Google.Cloud.PubSub.V1;
using Grpc.Core;
using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Pubsub.Tests;

/// <summary>
/// GH-5126: <c>UsePubsubUsingNamedConnection</c> / <c>AddNamedPubsubBrokerUsingNamedConnection</c> read the
/// Pub/Sub <b>emulator</b> endpoint from <c>ConnectionStrings:{name}</c> once the container is built. The
/// CommunityToolkit Aspire Floci GCP emulator (<c>builder.AddFlociGcp("gcp")</c>) supplies <c>http://host:port</c>.
/// </summary>
public class named_connection_5126
{
    private static readonly BrokerName theSecondary = new("secondary");

    private static Task<IHost> startStubbedAsync(Dictionary<string, string?> connectionStrings,
        Action<WolverineOptions> configure)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(connectionStrings))
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.StubAllExternalTransports();
            }).StartAsync();
    }

    private static async Task<(string? Endpoint, ChannelCredentials? Credentials)[]> builderSettingsOf(
        PubsubTransport transport)
    {
        var publisher = new PublisherServiceApiClientBuilder();
        await transport.ConfigurePublisherApiBuilder!(publisher);

        var subscriberApi = new SubscriberServiceApiClientBuilder();
        await transport.ConfigureSubscriberApiBuilder!(subscriberApi);

        var subscriber = new SubscriberClientBuilder();
        await transport.ConfigureSubscriberClientBuilder!(subscriber);

        return
        [
            (publisher.Endpoint, publisher.ChannelCredentials),
            (subscriberApi.Endpoint, subscriberApi.ChannelCredentials),
            (subscriber.Endpoint, subscriber.ChannelCredentials)
        ];
    }

    [Fact]
    public async Task missing_named_connection_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await startStubbedAsync([],
                opts => opts.UsePubsubUsingNamedConnection("wolverine", "gcp"));
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["gcp"]);
        ex.Message.ShouldContain("builder.AddFlociGcp(\"gcp\")");
    }

    [Fact]
    public async Task missing_named_connection_for_named_broker_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await startStubbedAsync([],
                opts => opts.AddNamedPubsubBrokerUsingNamedConnection(theSecondary, "other-project", "gcp2"));
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["gcp2"]);
        ex.Message.ShouldContain("builder.AddFlociGcp(\"gcp2\")");
    }

    [Theory]
    [InlineData("http://emulator-host:4588", "emulator-host:4588", false)] // CommunityToolkit Aspire Floci GCP
    [InlineData("https://emulator-host:4588", "emulator-host:4588", true)]
    [InlineData("emulator-host:8085", "emulator-host:8085", false)] // PUBSUB_EMULATOR_HOST format
    public async Task applies_the_named_connection_to_the_default_broker(string value, string endpoint, bool tls)
    {
        using var host = await startStubbedAsync(new() { ["ConnectionStrings:gcp"] = value },
            opts => opts.UsePubsubUsingNamedConnection("wolverine", "gcp"));

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
        transport.ProjectId.ShouldBe("wolverine");
        transport.DescribeEndpoint().ShouldBe($"project: wolverine (emulator: {endpoint})");

        var expectedCredentials = tls ? ChannelCredentials.SecureSsl : ChannelCredentials.Insecure;
        foreach (var setting in await builderSettingsOf(transport))
        {
            setting.Endpoint.ShouldBe(endpoint);
            setting.Credentials.ShouldBeSameAs(expectedCredentials);
        }
    }

    [Fact]
    public async Task applies_the_named_connection_to_a_named_broker()
    {
        using var host = await startStubbedAsync(new()
        {
            ["ConnectionStrings:gcp"] = "http://primary:8085",
            ["ConnectionStrings:gcp2"] = "http://secondary-host:8086"
        }, opts =>
        {
            opts.UsePubsubUsingNamedConnection("wolverine", "gcp");
            opts.AddNamedPubsubBrokerUsingNamedConnection(theSecondary, "other-project", "gcp2");
        });

        var transports = host.GetRuntime().Options.Transports;

        var @default = transports.GetOrCreate<PubsubTransport>();
        (await builderSettingsOf(@default)).ShouldAllBe(x => x.Endpoint == "primary:8085");

        var named = transports.GetOrCreate<PubsubTransport>(theSecondary);
        named.ProjectId.ShouldBe("other-project");
        (await builderSettingsOf(named)).ShouldAllBe(x => x.Endpoint == "secondary-host:8086");
    }

    [Fact]
    public async Task explicit_client_configuration_still_wins()
    {
        using var host = await startStubbedAsync(new() { ["ConnectionStrings:gcp"] = "http://from-config:8085" },
            opts => opts.UsePubsubUsingNamedConnection("wolverine", "gcp")
                .ConfigurePublisherApiClient(b => b.Endpoint = "overridden:8085"));

        var transport = host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
        var settings = await builderSettingsOf(transport);
        settings[0].Endpoint.ShouldBe("overridden:8085");
        settings[1].Endpoint.ShouldBe("from-config:8085");
    }
}

public class named_connection_5126_end_to_end
{
    [Fact]
    public async Task round_trips_messages_through_named_connections()
    {
        if (!await TestingExtensions.IsEmulatorAvailable())
        {
            Assert.Skip("The GCP Pub/Sub emulator is not running on localhost:8085");
        }

        var topic = $"named-connection-{Guid.NewGuid():N}";
        var secondaryTopic = $"named-connection-2-{Guid.NewGuid():N}";
        var secondary = new BrokerName("secondary");

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                // What an Aspire AppHost supplies through builder.AddFlociGcp("gcp") + WithReference(...)
                ["ConnectionStrings:gcp"] = $"http://{TestingExtensions.EmulatorHost}",
                ["ConnectionStrings:gcp2"] = TestingExtensions.EmulatorHost
            }))
            .UseWolverine(opts =>
            {
                #region sample_pubsub_using_named_connection

                // Points Wolverine at the Pub/Sub emulator whose endpoint (e.g. "http://localhost:8085")
                // is the "gcp" connection string in IConfiguration
                opts.UsePubsubUsingNamedConnection("wolverine", "gcp")
                    .AutoProvision();

                #endregion

                #region sample_pubsub_named_broker_using_named_connection

                // A second, named Pub/Sub broker on another project, whose emulator endpoint is the
                // "gcp2" connection string
                opts.AddNamedPubsubBrokerUsingNamedConnection(new BrokerName("secondary"), "wolverine-named", "gcp2")
                    .AutoProvision();

                #endregion

                opts.Policies.DisableConventionalLocalRouting();

                opts.PublishMessage<NamedConnectionPubsubMessage>().ToPubsubTopic(topic);
                opts.ListenToPubsubTopic(topic);

                opts.PublishMessage<NamedConnectionPubsubMessage2>().ToPubsubTopicOnNamedBroker(secondary, secondaryTopic);
                opts.ListenToPubsubTopicOnNamedBroker(secondary, secondaryTopic);

                opts.Discovery.DisableConventionalDiscovery().IncludeType<NamedConnectionPubsubHandler>();
            }).StartAsync(TestContext.Current.CancellationToken);

        var session = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<NamedConnectionPubsubMessage>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedConnectionPubsubMessage("default")));

        session.Received.SingleMessage<NamedConnectionPubsubMessage>().Id.ShouldBe("default");

        var session2 = await host
            .TrackActivity()
            .Timeout(60.Seconds())
            .WaitForMessageToBeReceivedAt<NamedConnectionPubsubMessage2>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new NamedConnectionPubsubMessage2("named")));

        var received = session2.Received.SingleEnvelope<NamedConnectionPubsubMessage2>();
        received.Message.ShouldBeOfType<NamedConnectionPubsubMessage2>().Id.ShouldBe("named");
        received.Destination!.Scheme.ShouldBe("secondary");
    }
}

public record NamedConnectionPubsubMessage(string Id);

public record NamedConnectionPubsubMessage2(string Id);

public class NamedConnectionPubsubHandler
{
    public void Handle(NamedConnectionPubsubMessage message)
    {
    }

    public void Handle(NamedConnectionPubsubMessage2 message)
    {
    }
}
