using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Nats.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Nats.Tests;

/// <summary>
/// GH-5126. <c>UseNatsUsingNamedConnection(name)</c> / <c>AddNamedNatsBrokerUsingNamedConnection(broker, name)</c>
/// read the NATS URL from <c>ConnectionStrings:{name}</c>, which is where a .NET Aspire AppHost's
/// <c>builder.AddNats("name")</c> + <c>.WithReference(...)</c> puts it. None of these need a running broker.
/// </summary>
public class named_connection_5126
{
    private static IHostBuilder hostWith(Action<WolverineOptions> configure,
        params (string Name, string Value)[] connectionStrings)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                connectionStrings.Select(x =>
                    new KeyValuePair<string, string?>($"ConnectionStrings:{x.Name}", x.Value))))
            .UseWolverine(opts =>
            {
                configure(opts);
                opts.StubAllExternalTransports();
            });
    }

    private static NatsTransport transportFor(IHost host, string protocol = NatsTransport.ProtocolName)
    {
        return host.GetRuntime().Options.Transports.OfType<NatsTransport>().Single(x => x.Protocol == protocol);
    }

    [Fact]
    public async Task a_missing_name_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts => opts.UseNatsUsingNamedConnection("nats")).StartAsync();
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["nats"]);
        ex.Message.ShouldContain("builder.AddNats(\"nats\")");
    }

    [Fact]
    public async Task a_missing_name_for_a_named_broker_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
            {
                opts.UseNatsUsingNamedConnection("nats");
                opts.AddNamedNatsBrokerUsingNamedConnection(new BrokerName("secondary"), "nats2");
            }, ("nats", "nats://localhost:4222")).StartAsync();
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["nats2"]);
        ex.Message.ShouldContain("builder.AddNats(\"nats2\")");
    }

    [Theory]
    [InlineData("nats://localhost:4222")]
    // The shape Aspire.Hosting.Nats produces when the resource has a password parameter
    [InlineData("nats://nats:s3cr3t@localhost:61234")]
    public async Task the_named_connection_string_is_applied_to_the_default_broker(string value)
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:nats", value)]))
            .UseWolverine(opts =>
            {
                #region sample_nats_using_named_connection

                // "nats" is the name of the NATS resource in the Aspire AppHost,
                // i.e. builder.AddNats("nats"), referenced by this project
                opts.UseNatsUsingNamedConnection("nats");

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).Configuration.ConnectionString.ShouldBe(value);

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_named_connection_string_wins_over_one_set_by_configure()
    {
        using var host = await hostWith(opts => opts.UseNatsUsingNamedConnection("nats", c =>
            {
                c.ConnectionString = "nats://wrong:4222";
                c.EnableJetStream = false;
            }),
            ("nats", "nats://localhost:4222")).StartAsync(TestContext.Current.CancellationToken);

        var configuration = transportFor(host).Configuration;
        configuration.ConnectionString.ShouldBe("nats://localhost:4222");
        configuration.EnableJetStream.ShouldBeFalse();

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_named_connection_string_is_applied_to_a_named_broker()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("ConnectionStrings:nats", "nats://localhost:4222"),
                new KeyValuePair<string, string?>("ConnectionStrings:nats-audit", "nats://audit:5222")
            ]))
            .UseWolverine(opts =>
            {
                #region sample_nats_named_broker_using_named_connection

                opts.UseNatsUsingNamedConnection("nats");

                // A second, independent NATS broker whose URL comes from
                // builder.AddNats("nats-audit") in the Aspire AppHost
                opts.AddNamedNatsBrokerUsingNamedConnection(new BrokerName("audit"), "nats-audit");

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).Configuration.ConnectionString.ShouldBe("nats://localhost:4222");
        transportFor(host, "audit").Configuration.ConnectionString.ShouldBe("nats://audit:5222");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }
}

/// <summary>
/// GH-5126. A named connection string really reaches the broker: one host publishes and listens on NATS with its
/// URL taken from <c>ConnectionStrings:nats</c>.
/// </summary>
[Collection("NATS Integration")]
[Trait("Category", "Integration")]
public class named_connection_end_to_end_5126
{
    private readonly NatsContainerFixture _fixture;

    public named_connection_end_to_end_5126(NatsContainerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task round_trips_a_message_through_a_named_connection()
    {
        var subject = $"named-connection.{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:nats", _fixture.ConnectionString)]))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NamedConnection5126";
                opts.UseNatsUsingNamedConnection("nats", c => c.EnableJetStream = false);

                opts.PublishMessage<OrderPlaced>().ToNatsSubject(subject);
                opts.ListenToNatsSubject(subject);
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<OrderPlaced>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new OrderPlaced("named-connection")));

        session.Received.SingleMessage<OrderPlaced>().OrderId.ShouldBe("named-connection");
    }
}
