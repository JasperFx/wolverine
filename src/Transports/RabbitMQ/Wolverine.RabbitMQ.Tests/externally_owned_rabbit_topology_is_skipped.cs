using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.RabbitMQ.Internal;
using Xunit;

namespace Wolverine.RabbitMQ.Tests;

// GH-3064: a queue or exchange marked ExternallyOwned() must never be declared (created) at startup
// nor deleted during resource teardown, even with AutoProvision on - the escape hatch for topology
// owned by another system where the calling identity lacks configure/delete permissions. Assertions
// hit the broker directly (passive declare on a separate admin connection) rather than relying on
// Wolverine's in-memory model.
public class externally_owned_rabbit_topology_is_skipped : IAsyncLifetime
{
    private readonly string _externalQueue = "ext-queue-" + Guid.NewGuid().ToString("N");
    private readonly string _externalExchange = "ext-exchange-" + Guid.NewGuid().ToString("N");
    private readonly string _ownedQueue = "owned-queue-" + Guid.NewGuid().ToString("N");
    private readonly string _passiveExchange = "passive-exchange-" + Guid.NewGuid().ToString("N");

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        try
        {
            await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
            await using var channel = await conn.CreateChannelAsync();
            foreach (var q in new[] { _externalQueue, _ownedQueue })
            {
                try { await channel.QueueDeleteAsync(q, false, false); } catch { }
            }
            foreach (var e in new[] { _externalExchange, _passiveExchange })
            {
                try { await channel.ExchangeDeleteAsync(e); } catch { }
            }
        }
        catch
        {
            // ignore cleanup failures
        }
    }

    // --- broker-side existence checks via a separate admin connection ---

    private static async Task<bool> QueueExistsAsync(string name)
    {
        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        try { await channel.QueueDeclarePassiveAsync(name); return true; }
        catch { return false; } // passive declare on a missing queue closes the channel with a 404
    }

    private static async Task<bool> ExchangeExistsAsync(string name)
    {
        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        try { await channel.ExchangeDeclarePassiveAsync(name); return true; }
        catch { return false; }
    }

    private static async Task PreCreateQueueAsync(string name)
    {
        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        await channel.QueueDeclareAsync(name, durable: true, exclusive: false, autoDelete: false);
    }

    private static async Task PreCreateExchangeAsync(string name)
    {
        await using var conn = await new ConnectionFactory { HostName = "localhost" }.CreateConnectionAsync();
        await using var channel = await conn.CreateChannelAsync();
        await channel.ExchangeDeclareAsync(name, type: "fanout", durable: true, autoDelete: false);
    }

    // === Setup: externally-owned topology is never created ===

    [Fact]
    public async Task externally_owned_queue_is_not_created_at_startup()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
            opts.PublishMessage<ExtMessage>().ToRabbitQueue(_externalQueue).ExternallyOwned();
        });

        (await QueueExistsAsync(_externalQueue)).ShouldBeFalse();
    }

    [Fact]
    public async Task externally_owned_exchange_is_not_created_at_startup()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
            opts.PublishMessage<ExtMessage>().ToRabbitExchange(_externalExchange).ExternallyOwned();
        });

        (await ExchangeExistsAsync(_externalExchange)).ShouldBeFalse();
    }

    [Fact]
    public async Task owned_topology_is_still_created_alongside_externally_owned()
    {
        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().AutoProvision();
            opts.PublishMessage<ExtMessage>().ToRabbitQueue(_externalQueue).ExternallyOwned();
            opts.PublishMessage<OwnedMessage>().ToRabbitQueue(_ownedQueue); // owned -> should be created
        });

        (await QueueExistsAsync(_ownedQueue)).ShouldBeTrue();
        (await QueueExistsAsync(_externalQueue)).ShouldBeFalse();
    }

    // === Teardown: externally-owned topology survives ===

    [Fact]
    public async Task teardown_leaves_an_externally_owned_listener_queue_alone()
    {
        await PreCreateQueueAsync(_externalQueue);

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq();
            opts.ListenToRabbitQueue(_externalQueue).ExternallyOwned();
        });

        var queue = host.Get<WolverineOptions>().RabbitMqTransport().Queues[_externalQueue];
        await queue.TeardownAsync(NullLogger.Instance);

        (await QueueExistsAsync(_externalQueue)).ShouldBeTrue();
    }

    [Fact]
    public async Task teardown_leaves_an_externally_owned_exchange_alone()
    {
        await PreCreateExchangeAsync(_externalExchange);

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq();
            opts.PublishMessage<ExtMessage>().ToRabbitExchange(_externalExchange).ExternallyOwned();
        });

        var exchange = host.Get<WolverineOptions>().RabbitMqTransport().Exchanges[_externalExchange];
        await exchange.TeardownAsync(NullLogger.Instance);

        (await ExchangeExistsAsync(_externalExchange)).ShouldBeTrue();
    }

    // === Q3: DeclarePassive exchanges must also survive teardown (no longer deleted) ===

    [Fact]
    public async Task teardown_leaves_a_declare_passive_exchange_alone()
    {
        await PreCreateExchangeAsync(_passiveExchange);

        using var host = await WolverineHost.ForAsync(opts =>
        {
            opts.PublishMessage<ExtMessage>().ToRabbitExchange(_passiveExchange, ex => ex.DeclarePassive = true);
        });

        var exchange = host.Get<WolverineOptions>().RabbitMqTransport().Exchanges[_passiveExchange];
        await exchange.TeardownAsync(NullLogger.Instance);

        (await ExchangeExistsAsync(_passiveExchange)).ShouldBeTrue();
    }
}

public record ExtMessage(string Name);

public record OwnedMessage(string Name);
