using Wolverine.ComplianceTests.Compliance;
using Wolverine.Redis;

namespace Wolverine.Redis.Tests;

public class RedisTransportFixture : TransportComplianceFixture
{
    public RedisTransportFixture() : base(new Uri($"redis://{RedisContainerFixture.ConnectionString}?streamKey=wolverine-tests-{Guid.NewGuid():N}"))
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseRedisTransport(RedisContainerFixture.ConnectionString)
                .AutoProvision();
        });

        await ReceiverIs(opts =>
        {
            opts.UseRedisTransport(RedisContainerFixture.ConnectionString)
                .AutoProvision();
                
            opts.ListenToRedisStream("wolverine-tests", "test-consumer-group");
        });
    }
}
