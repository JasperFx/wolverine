using System;
using System.Threading.Tasks;
using Wolverine.ComplianceTests.Compliance;
using Wolverine.Configuration;
using Wolverine.Redis;
using Xunit;

namespace Wolverine.Redis.Tests;

public class RedisInlineComplianceFixture : TransportComplianceFixture, IAsyncLifetime
{
    public RedisInlineComplianceFixture() : base(new Uri("redis://stream/0/receiver"), 120)
    {
    }

    public async Task InitializeAsync()
    {
        var receiverStream = $"wolverine-tests-inline-receiver-{Guid.NewGuid():N}";
        OutboundAddress = new Uri($"redis://stream/0/{receiverStream}");

        await SenderIs(opts =>
        {
            opts.UseRedisTransport("localhost:6379").AutoProvision();
            opts.PublishAllMessages().ToRedisStream(receiverStream).SendInline();
        });

        await ReceiverIs(opts =>
        {
            opts.UseRedisTransport("localhost:6379").AutoProvision();
            opts.ListenToRedisStream(receiverStream, "g1").ProcessInline();
        });
    }

    public new Task DisposeAsync()
    {
        return Task.CompletedTask;
    }
}

public class InlineSendingAndReceivingCompliance : TransportCompliance<RedisInlineComplianceFixture>;

