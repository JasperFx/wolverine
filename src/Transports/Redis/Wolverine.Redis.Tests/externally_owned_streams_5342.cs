using JasperFx;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using StackExchange.Redis;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// GH-5342. A Redis stream another service owns: Wolverine should create this application's consumer group on it,
/// but never the stream, and an endpoint marked externally owned as a whole should never create, delete or purge
/// its stream or consumer group. Purging a Redis stream deletes its key, so a purge on startup would delete the
/// other service's stream outright.
/// </summary>
public class externally_owned_streams_5342 : IAsyncLifetime
{
    private ConnectionMultiplexer theConnection = null!;
    private IDatabase theDatabase = null!;
    private readonly string theSuffix = Guid.NewGuid().ToString("N")[..8];

    public async ValueTask InitializeAsync()
    {
        theConnection = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);
        theDatabase = theConnection.GetDatabase();
    }

    public async ValueTask DisposeAsync()
    {
        await theConnection.DisposeAsync();
    }

    private string key(string prefix) => $"{prefix}-{theSuffix}";

    private async Task<bool> groupExistsAsync(string streamKey, string group)
    {
        if (!await theDatabase.KeyExistsAsync(streamKey)) return false;

        var groups = await theDatabase.StreamGroupInfoAsync(streamKey);
        return groups.Any(x => x.Name == group);
    }

    [Fact]
    public async Task an_externally_owned_stream_is_not_created_but_the_consumer_group_is()
    {
        var orders = key("orders");

        // The publishing service owns the stream, and has already written to it
        await theDatabase.StreamAddAsync(orders, "data", "existing");

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Wolverine still provisions everything this application owns...
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();

                #region sample_redis_stream_is_externally_owned

                opts.ListenToRedisStream(orders, "shipping")

                    // The stream belongs to the service that publishes to it. Wolverine
                    // creates the "shipping" consumer group on that stream, but never
                    // creates the stream itself
                    .StreamIsExternallyOwned();

                #endregion
            }).StartAsync(TestContext.Current.CancellationToken);

        (await groupExistsAsync(orders, "shipping")).ShouldBeTrue();
        (await theDatabase.StreamLengthAsync(orders)).ShouldBe(1);
    }

    [Fact]
    public async Task a_missing_externally_owned_stream_is_named_and_not_created()
    {
        var payments = key("payments");

        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // Give up after the first attempt rather than retrying for two minutes
                opts.BrokerInitializationTimeout = 1.Seconds();

                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
                opts.ListenToRedisStream(payments, "shipping").StreamIsExternallyOwned();
            });

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await builder.StartAsync(TestContext.Current.CancellationToken);
        });

        var message = flatten(ex);
        message.ShouldContain($"stream '{payments}'");
        message.ShouldContain("externally owned");

        (await theDatabase.KeyExistsAsync(payments)).ShouldBeFalse();
    }

    [Fact]
    public async Task an_externally_owned_endpoint_is_never_created_purged_or_torn_down()
    {
        var billing = key("billing");
        var audit = key("audit");
        var notifications = key("notifications");

        // Another service owns the "billing" stream and the consumer group this one reads through, and the
        // "audit" stream this one only publishes to
        await theDatabase.StreamAddAsync(billing, "data", "existing");
        await theDatabase.StreamCreateConsumerGroupAsync(billing, "billing", "0-0");
        await theDatabase.StreamAddAsync(audit, "data", "existing");

        IHostBuilder configure() => Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseRedisTransport(RedisContainerFixture.ConnectionString)
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                #region sample_redis_endpoint_is_externally_owned

                // The stream and the "billing" consumer group belong to another service.
                // Wolverine reads through the group, but never creates, deletes or purges
                // either of them
                opts.ListenToRedisStream(billing, "billing")
                    .ExternallyOwned();

                // A stream another service owns and this one only publishes to
                opts.PublishMessage<RedisAuditEntryRecorded>()
                    .ToRedisStream(audit)
                    .ExternallyOwned();

                #endregion

                // Owned by this application, so provisioned as usual
                opts.ListenToRedisStream(notifications, "notifications");
            });

        using (var host = await configure().StartAsync(TestContext.Current.CancellationToken))
        {
            // AutoPurgeOnStartup deletes the keys of the streams Wolverine owns, but not these
            (await theDatabase.StreamLengthAsync(billing)).ShouldBe(1);
            (await theDatabase.StreamLengthAsync(audit)).ShouldBe(1);
            (await groupExistsAsync(notifications, "notifications")).ShouldBeTrue();

            await host.StopAsync(TestContext.Current.CancellationToken);
        }

        (await configure().RunJasperFxCommands(["resources", "check"])).ShouldBe(0);
        (await configure().RunJasperFxCommands(["resources", "teardown"])).ShouldBe(0);

        // What this application owns is gone, and what it doesn't is still there
        (await groupExistsAsync(notifications, "notifications")).ShouldBeFalse();
        (await groupExistsAsync(billing, "billing")).ShouldBeTrue();
    }

    [Fact]
    public async Task a_missing_consumer_group_on_an_externally_owned_endpoint_is_named_and_not_created()
    {
        var invoices = key("invoices");
        await theDatabase.StreamAddAsync(invoices, "data", "existing");

        var builder = Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.BrokerInitializationTimeout = 1.Seconds();

                opts.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
                opts.ListenToRedisStream(invoices, "billing").ExternallyOwned();
            });

        var ex = await Should.ThrowAsync<Exception>(async () =>
        {
            using var host = await builder.StartAsync(TestContext.Current.CancellationToken);
        });

        var message = flatten(ex);
        message.ShouldContain("consumer group 'billing'");
        message.ShouldContain("externally owned");

        (await groupExistsAsync(invoices, "billing")).ShouldBeFalse();
    }

    private static string flatten(Exception exception)
    {
        var messages = new List<string>();
        for (var e = exception; e != null; e = e.InnerException)
        {
            messages.Add(e.Message);
        }

        if (exception is AggregateException aggregate)
        {
            messages.AddRange(aggregate.InnerExceptions.Select(flatten));
        }

        return string.Join(Environment.NewLine, messages);
    }
}

public record RedisAuditEntryRecorded;
