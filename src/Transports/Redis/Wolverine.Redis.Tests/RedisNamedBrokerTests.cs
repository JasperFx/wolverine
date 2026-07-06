using System;
using System.Linq;
using System.Threading.Tasks;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Redis.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// Registration-level coverage for named Redis brokers that needs no running broker, so it always runs in
/// CI. A named broker is a second, independent <see cref="RedisTransport"/> whose <c>Protocol</c> (and
/// therefore its endpoints' URI scheme) is the broker name, so its endpoints never collide with the default
/// <c>redis://</c> broker.
/// </summary>
public class RedisNamedBrokerRegistrationTests
{
    private readonly BrokerName theName = new("secondary");

    [Fact]
    public void adds_a_distinct_transport_instance_per_broker()
    {
        var options = new WolverineOptions();
        options.UseRedisTransport("localhost:6379");
        options.AddNamedRedisBroker(theName, "localhost:6399");

        var transports = options.Transports.OfType<RedisTransport>().ToList();
        transports.Count.ShouldBe(2);
        transports.Select(x => x.Protocol).OrderBy(x => x).ShouldBe(["redis", "secondary"]);
    }

    [Fact]
    public void named_broker_endpoints_use_the_broker_name_as_their_uri_scheme()
    {
        var options = new WolverineOptions();
        options.UseRedisTransport("localhost:6379");
        options.AddNamedRedisBroker(theName, "localhost:6399");

        var named = options.Transports.OfType<RedisTransport>().Single(x => x.Protocol == "secondary");
        named.StreamEndpoint("orders").Uri.ShouldBe(new Uri("secondary://stream/0/orders"));

        // ...and the default broker keeps the canonical redis:// scheme.
        var @default = options.Transports.OfType<RedisTransport>().Single(x => x.Protocol == "redis");
        @default.StreamEndpoint("orders").Uri.ShouldBe(new Uri("redis://stream/0/orders"));
    }

    [Fact]
    public void listening_on_an_unregistered_named_broker_throws()
    {
        var options = new WolverineOptions();
        options.UseRedisTransport("localhost:6379");

        Should.Throw<InvalidOperationException>(() =>
            options.ListenToRedisStreamOnNamedBroker(theName, "orders", "g1"));
    }
}

/// <summary>
/// End-to-end coverage that a named Redis broker actually talks to a <em>different</em> server than the
/// default one. The default broker is server A (the shared container fixture) and the named broker is a
/// second Testcontainers Redis (server B). Proves:
/// <list type="bullet">
/// <item>a message published on the named broker lands on <b>server B</b> and not server A,</item>
/// <item>a default publish lands on <b>server A</b> and not server B, and</item>
/// <item>a message published <b>and consumed</b> on the named broker round-trips and arrives stamped with
/// the broker's own URI scheme (the receive pipeline sets <c>Destination</c> from the listener endpoint's
/// URI).</item>
/// </list>
/// </summary>
public class RedisNamedBrokerTests : IClassFixture<SecondRedisServerFixture>
{
    private readonly BrokerName theName = new("secondary");
    private readonly string _serverAConn = RedisContainerFixture.ConnectionString;
    private readonly string _serverBConn;
    private readonly bool _skip;

    public RedisNamedBrokerTests(SecondRedisServerFixture serverB)
    {
        _serverBConn = serverB.ConnectionString!;
        _skip = serverB.ConnectionString == null;
    }

    [Fact]
    public async Task named_broker_send_lands_on_the_named_broker()
    {
        if (_skip) return;

        var streamKey = $"named-{Guid.NewGuid():N}";

        using var host = await BuildSenderAsync(streamKey, useNamedBroker: true);

        await host.MessageBus().SendAsync(new RedisBrokerMessage("on-named-broker"));

        // Landed on server B (the named broker's connection)...
        var onB = await RedisMultiBrokerHelpers.WaitForStreamAsync(_serverBConn, streamKey, 15.Seconds());
        onB.Length.ShouldBe(1);

        // ...and NOT on the default server A.
        var onA = await RedisMultiBrokerHelpers.ReadStreamAsync(_serverAConn, streamKey);
        onA.Length.ShouldBe(0);
    }

    [Fact]
    public async Task default_broker_send_lands_on_the_default_broker()
    {
        if (_skip) return;

        var streamKey = $"named-{Guid.NewGuid():N}";

        using var host = await BuildSenderAsync(streamKey, useNamedBroker: false);

        await host.MessageBus().SendAsync(new RedisBrokerMessage("on-default-broker"));

        var onA = await RedisMultiBrokerHelpers.WaitForStreamAsync(_serverAConn, streamKey, 15.Seconds());
        onA.Length.ShouldBe(1);

        var onB = await RedisMultiBrokerHelpers.ReadStreamAsync(_serverBConn, streamKey);
        onB.Length.ShouldBe(0);
    }

    [Fact]
    public async Task round_trips_a_message_over_the_named_broker()
    {
        if (_skip) return;

        var streamKey = $"named-{Guid.NewGuid():N}";

        // A single host both publishes and listens on the named broker (server B). On receipt the pipeline
        // stamps Destination from the listener endpoint's URI, so the consumed envelope carries the named
        // broker's "secondary" scheme rather than the default "redis".
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NamedBrokerInbound";
                opts.UseRedisTransport(_serverAConn).AutoProvision();
                opts.AddNamedRedisBroker(theName, _serverBConn).AutoProvision();

                opts.PublishMessage<RedisBrokerMessage>().ToRedisStreamOnNamedBroker(theName, streamKey).SendInline();
                opts.ListenToRedisStreamOnNamedBroker(theName, streamKey, "named-group");
            })
            .StartAsync();

        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<RedisBrokerMessage>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new RedisBrokerMessage("round-trip")));

        var received = session.Received.SingleEnvelope<RedisBrokerMessage>();
        received.Message.ShouldBeOfType<RedisBrokerMessage>().Id.ShouldBe("round-trip");
        received.Destination!.Scheme.ShouldBe("secondary");
    }

    /// <summary>
    /// Both brokers are always registered and connected (server A default, server B named), so "not on the
    /// other server" is a meaningful assertion. Only <b>one</b> publish rule is registered per host — to the
    /// named broker or the default — so a single message send targets exactly one server.
    /// </summary>
    private Task<IHost> BuildSenderAsync(string streamKey, bool useNamedBroker)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NamedBrokerSender";
                opts.UseRedisTransport(_serverAConn).AutoProvision();
                opts.AddNamedRedisBroker(theName, _serverBConn).AutoProvision();

                opts.Policies.DisableConventionalLocalRouting();

                if (useNamedBroker)
                {
                    opts.PublishMessage<RedisBrokerMessage>().ToRedisStreamOnNamedBroker(theName, streamKey).SendInline();
                }
                else
                {
                    opts.PublishMessage<RedisBrokerMessage>().ToRedisStream(streamKey).SendInline();
                }
            })
            .StartAsync();
    }
}
