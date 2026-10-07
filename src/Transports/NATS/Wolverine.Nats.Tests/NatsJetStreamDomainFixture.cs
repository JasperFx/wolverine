using System.Text;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Networks;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Net;
using Testcontainers.Nats;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// A hub and a leaf node, each running its own JetStream domain -- the usual way to put JetStream at the
/// edge. A client connected to the leaf reaches the leaf's JetStream through the plain <c>$JS.API</c> and the
/// hub's only through <c>$JS.hub.API</c>, so anything that ignores <c>UseJetStreamDomain("hub")</c> lands
/// in the wrong JetStream and is observable there. A single server cannot show that: it answers both API
/// prefixes for its own domain.
/// </summary>
public class NatsJetStreamDomainFixture : IAsyncLifetime
{
    public const string HubDomain = "hub";
    public const string LeafDomain = "leaf";

    private const string ConfigPath = "/etc/nats/wolverine-test.conf";

    private INetwork? _network;
    private NatsContainer? _hub;
    private NatsContainer? _leaf;

    /// <summary>
    /// Clients connect here, to the leaf node
    /// </summary>
    public string LeafConnectionString { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        _network = new NetworkBuilder().WithName($"wolverine-nats-domain-{Guid.NewGuid():N}").Build();
        await _network.CreateAsync();

        _hub = new NatsBuilder()
            .WithImage(NatsContainerFixture.NatsImage)
            .WithNetwork(_network)
            .WithNetworkAliases("hub")
            .WithResourceMapping(Encoding.UTF8.GetBytes(
                $$"""
                  jetstream { domain: {{HubDomain}}, store_dir: "/tmp/nats/hub" }
                  leafnodes { port: 7422 }
                  """), ConfigPath)
            .WithCommand("--config", ConfigPath)
            .Build();
        await _hub.StartAsync();

        _leaf = new NatsBuilder()
            .WithImage(NatsContainerFixture.NatsImage)
            .WithNetwork(_network)
            .WithResourceMapping(Encoding.UTF8.GetBytes(
                $$"""
                  jetstream { domain: {{LeafDomain}}, store_dir: "/tmp/nats/leaf" }
                  leafnodes { remotes: [ { url: "nats://hub:7422" } ] }
                  """), ConfigPath)
            .WithCommand("--config", ConfigPath)
            .Build();
        await _leaf.StartAsync();

        LeafConnectionString = _leaf.GetConnectionString();

        await waitForTheHubThroughTheLeafAsync();
    }

    /// <summary>
    /// A JetStream context on a connection to the leaf, scoped to the given domain
    /// </summary>
    public static INatsJSContext JetStreamFor(NatsConnection connection, string domain)
    {
        return connection.CreateJetStreamContext(new NatsJSOpts(connection.Opts, domain: domain));
    }

    // The leaf reports healthy before its leaf connection to the hub is up
    private async Task waitForTheHubThroughTheLeafAsync()
    {
        await using var connection = new NatsConnection(new NatsOpts { Url = LeafConnectionString });
        await connection.ConnectAsync();
        var hub = JetStreamFor(connection, HubDomain);

        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            try
            {
                await hub.GetAccountInfoAsync();
                return;
            }
            catch (Exception) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(250);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_leaf != null) await _leaf.DisposeAsync();
        if (_hub != null) await _hub.DisposeAsync();
        if (_network != null) await _network.DeleteAsync();
    }
}
