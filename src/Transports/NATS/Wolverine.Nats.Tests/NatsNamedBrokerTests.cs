using IntegrationTests;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Testcontainers.Nats;
using Wolverine.Nats.Internal;
using Wolverine.Tracking;
using Xunit;
using Xunit.Abstractions;

namespace Wolverine.Nats.Tests;

/// <summary>
/// Registration-level coverage for named NATS brokers that needs no running broker, so it always runs in CI.
/// A named broker is a second, independent <see cref="NatsTransport"/> whose <c>Protocol</c> (and therefore
/// its endpoints' URI scheme) is the broker name, so its endpoints never collide with the default
/// <c>nats://</c> broker.
/// </summary>
public class NatsNamedBrokerRegistrationTests
{
    private readonly BrokerName theName = new("secondary");

    [Fact]
    public void adds_a_distinct_transport_instance_per_broker()
    {
        var options = new WolverineOptions();
        options.UseNats("nats://localhost:4222");
        options.AddNamedNatsBroker(theName, "nats://localhost:5222");

        var transports = options.Transports.OfType<NatsTransport>().ToList();
        transports.Count.ShouldBe(2);
        transports.Select(x => x.Protocol).OrderBy(x => x).ShouldBe(["nats", "secondary"]);
    }

    [Fact]
    public void named_broker_endpoints_use_the_broker_name_as_their_uri_scheme()
    {
        var options = new WolverineOptions();
        options.UseNats("nats://localhost:4222");
        options.AddNamedNatsBroker(theName, "nats://localhost:5222");

        var named = options.Transports.OfType<NatsTransport>().Single(x => x.Protocol == "secondary");
        named.EndpointForSubject("orders.created").Uri.ShouldBe(new Uri("secondary://subject/orders.created"));

        // ...and the default broker keeps the canonical nats:// scheme.
        var @default = options.Transports.OfType<NatsTransport>().Single(x => x.Protocol == "nats");
        @default.EndpointForSubject("orders.created").Uri.ShouldBe(new Uri("nats://subject/orders.created"));
    }

    [Fact]
    public void configuration_action_overload_applies_to_the_named_broker_only()
    {
        var options = new WolverineOptions();
        options.UseNats("nats://localhost:4222");
        options.AddNamedNatsBroker(theName, cfg =>
        {
            cfg.ConnectionString = "nats://localhost:5222";
            cfg.EnableJetStream = false;
        });

        var named = options.Transports.OfType<NatsTransport>().Single(x => x.Protocol == "secondary");
        named.Configuration.ConnectionString.ShouldBe("nats://localhost:5222");

        var @default = options.Transports.OfType<NatsTransport>().Single(x => x.Protocol == "nats");
        @default.Configuration.ConnectionString.ShouldBe("nats://localhost:4222");
    }
}

/// <summary>
/// End-to-end coverage that a named NATS broker actually talks to a <em>different</em> server than the
/// default one. Mirrors <see cref="NatsPerTenantConnectionTests"/>: the default broker is server A (from
/// NATS_URL / docker-compose) and the named broker is a second Testcontainers broker (server B). Proves:
/// <list type="bullet">
/// <item>a message published on the named broker lands on <b>server B</b> and not server A,</item>
/// <item>a default publish lands on <b>server A</b> and not server B, and</item>
/// <item>a message published <b>and consumed</b> on the named broker round-trips (exercising the named
/// broker's inbound envelope mapping, which stamps the broker's own URI scheme).</item>
/// </list>
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class NatsNamedBrokerTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly BrokerName theName = new("secondary");
    private NatsContainer? _serverB;
    private string _serverAUrl = null!;
    private string _serverBUrl = null!;
    private bool _skip;

    public NatsNamedBrokerTests(ITestOutputHelper output) => _output = output;

    public async Task InitializeAsync()
    {
        _serverAUrl = NatsTestHelpers.ResolveUrl();

        if (!await NatsTestHelpers.IsNatsAvailable(_serverAUrl))
        {
            _skip = true;
            return;
        }

        _serverB = new NatsBuilder().WithImage("nats:latest").Build();
        await _serverB.StartAsync();
        _serverBUrl = _serverB.GetConnectionString();

        _output.WriteLine($"Server A (default): {_serverAUrl}");
        _output.WriteLine($"Server B (named):   {_serverBUrl}");
    }

    public async Task DisposeAsync()
    {
        if (_serverB != null)
        {
            await _serverB.DisposeAsync();
        }
    }

    [Fact]
    public async Task named_broker_send_lands_on_the_named_broker()
    {
        if (_skip) return;

        var subject = $"named.{Guid.NewGuid():N}";

        await using var subOnB = await NatsTestHelpers.SubscribeRawAsync(_serverBUrl, subject);
        await using var subOnA = await NatsTestHelpers.SubscribeRawAsync(_serverAUrl, subject);

        using var host = await BuildSenderAsync(subject, useNamedBroker: true);

        await host.MessageBus().SendAsync(new OrderPlaced("on-named-broker"));

        // Landed on server B (the named broker's connection)...
        var received = await subOnB.ReadAsync(15.Seconds());
        received.ShouldNotBeNull();
        received!.Value.Subject.ShouldBe(subject);

        // ...and NOT on the default server A.
        (await subOnA.ReadAsync(2.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task default_broker_send_lands_on_the_default_broker()
    {
        if (_skip) return;

        var subject = $"named.{Guid.NewGuid():N}";

        await using var subOnA = await NatsTestHelpers.SubscribeRawAsync(_serverAUrl, subject);
        await using var subOnB = await NatsTestHelpers.SubscribeRawAsync(_serverBUrl, subject);

        using var host = await BuildSenderAsync(subject, useNamedBroker: false);

        await host.MessageBus().SendAsync(new OrderPlaced("on-default-broker"));

        var received = await subOnA.ReadAsync(15.Seconds());
        received.ShouldNotBeNull();
        received!.Value.Subject.ShouldBe(subject);

        (await subOnB.ReadAsync(2.Seconds())).ShouldBeNull();
    }

    [Fact]
    public async Task round_trips_a_message_over_the_named_broker()
    {
        if (_skip) return;

        var subject = $"named.{Guid.NewGuid():N}";

        // A single host both publishes and listens on the named broker (server B). The round-trip exercises
        // the named broker's inbound envelope mapping, which must stamp the "secondary" scheme (not "nats")
        // so Destination/reply routing resolves back to the right transport instance.
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NamedBrokerInbound";
                opts.UseNats(_serverAUrl);
                opts.AddNamedNatsBroker(theName, _serverBUrl);

                opts.PublishMessage<OrderPlaced>().ToNatsSubjectOnNamedBroker(theName, subject).SendInline();
                opts.ListenToNatsSubjectOnNamedBroker(theName, subject);
            })
            .StartAsync();

        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<OrderPlaced>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new OrderPlaced("round-trip")));

        var received = session.Received.SingleEnvelope<OrderPlaced>();
        received.Message.ShouldBeOfType<OrderPlaced>().OrderId.ShouldBe("round-trip");
        received.Destination!.Scheme.ShouldBe("secondary");
    }

    /// <summary>
    /// Both brokers are always registered and connected (server A default, server B named), so "not on the
    /// other server" is a meaningful assertion. Only <b>one</b> publish rule is registered per host — to the
    /// named broker or the default — so a single <c>OrderPlaced</c> send targets exactly one server.
    /// </summary>
    private Task<IHost> BuildSenderAsync(string subject, bool useNamedBroker)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureLogging(l => l.AddXunitLogging(_output))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NamedBrokerSender";
                opts.UseNats(_serverAUrl);
                opts.AddNamedNatsBroker(theName, _serverBUrl);

                opts.Policies.DisableConventionalLocalRouting();

                if (useNamedBroker)
                {
                    opts.PublishMessage<OrderPlaced>().ToNatsSubjectOnNamedBroker(theName, subject).SendInline();
                }
                else
                {
                    opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject).SendInline();
                }
            })
            .StartAsync();
    }
}
