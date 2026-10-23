using JasperFx.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Redis.Internal;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// GH-5126. <c>UseRedisTransportUsingNamedConnection(name)</c> / <c>AddNamedRedisBrokerUsingNamedConnection(broker, name)</c>
/// read the StackExchange.Redis connection string from <c>ConnectionStrings:{name}</c>, which is where a .NET Aspire
/// AppHost's <c>builder.AddRedis("name")</c> + <c>.WithReference(...)</c> puts it.
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

    private static RedisTransport transportFor(IHost host, string protocol = "redis")
    {
        return host.GetRuntime().Options.Transports.OfType<RedisTransport>().Single(x => x.Protocol == protocol);
    }

    [Fact]
    public async Task a_missing_name_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts => opts.UseRedisTransportUsingNamedConnection("redis")).StartAsync();
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["redis"]);
        ex.Message.ShouldContain("builder.AddRedis(\"redis\")");
    }

    [Fact]
    public async Task a_missing_name_for_a_named_broker_fails_startup()
    {
        var ex = await Should.ThrowAsync<MissingNamedConnectionStringsException>(async () =>
        {
            using var host = await hostWith(opts =>
            {
                opts.UseRedisTransportUsingNamedConnection("redis");
                opts.AddNamedRedisBrokerUsingNamedConnection(new BrokerName("secondary"), "redis2");
            }, ("redis", "localhost:6379")).StartAsync();
        });

        ex.Missing.Select(x => x.Name).ShouldBe(["redis2"]);
        ex.Message.ShouldContain("builder.AddRedis(\"redis2\")");
    }

    [Theory]
    [InlineData("localhost:6379")]
    // The shapes Aspire.Hosting.Redis produces: with its generated password, and with TLS enabled
    [InlineData("localhost:61234,password=s3cr3t")]
    [InlineData("localhost:61234,password=s3cr3t,ssl=true")]
    public async Task the_named_connection_string_is_applied_to_the_default_broker(string value)
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:redis", value)]))
            .UseWolverine(opts =>
            {
                #region sample_redis_using_named_connection

                // "redis" is the name of the Redis resource in the Aspire AppHost,
                // i.e. builder.AddRedis("redis"), referenced by this project
                opts.UseRedisTransportUsingNamedConnection("redis").AutoProvision();

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var transport = transportFor(host);
        transport.ConnectionString.ShouldBe(value);
        transport.DescribeEndpoint().ShouldBe("localhost:" + value.Split(',')[0].Split(':')[1]);
        transport.ConnectionSummary.ShouldNotContain("s3cr3t");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task the_named_connection_string_is_applied_to_a_named_broker()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("ConnectionStrings:redis", "localhost:6379"),
                new KeyValuePair<string, string?>("ConnectionStrings:redis-audit", "audit:6380,password=s3cr3t")
            ]))
            .UseWolverine(opts =>
            {
                #region sample_redis_named_broker_using_named_connection

                opts.UseRedisTransportUsingNamedConnection("redis");

                // A second, independent Redis broker whose connection string comes from
                // builder.AddRedis("redis-audit") in the Aspire AppHost
                opts.AddNamedRedisBrokerUsingNamedConnection(new BrokerName("audit"), "redis-audit");

                #endregion

                opts.StubAllExternalTransports();
            })
            .StartAsync(TestContext.Current.CancellationToken);

        transportFor(host).ConnectionString.ShouldBe("localhost:6379");
        transportFor(host, "audit").ConnectionString.ShouldBe("audit:6380,password=s3cr3t");

        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task round_trips_a_message_through_a_named_connection()
    {
        var streamKey = $"named-connection-{Guid.NewGuid():N}";

        using var host = await Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration(c => c.AddInMemoryCollection(
                [new KeyValuePair<string, string?>("ConnectionStrings:redis", RedisContainerFixture.ConnectionString)]))
            .UseWolverine(opts =>
            {
                opts.ServiceName = "NamedConnection5126";
                opts.UseRedisTransportUsingNamedConnection("redis").AutoProvision();

                opts.PublishMessage<RedisBrokerMessage>().ToRedisStream(streamKey);
                opts.ListenToRedisStream(streamKey, "named-connection-group");
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var session = await host
            .TrackActivity()
            .Timeout(30.Seconds())
            .WaitForMessageToBeReceivedAt<RedisBrokerMessage>(host)
            .ExecuteAndWaitAsync(c => c.SendAsync(new RedisBrokerMessage("named-connection")));

        session.Received.SingleMessage<RedisBrokerMessage>().Id.ShouldBe("named-connection");
    }
}
