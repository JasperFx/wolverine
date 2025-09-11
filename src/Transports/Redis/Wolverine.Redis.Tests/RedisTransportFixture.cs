using Wolverine.ComplianceTests.Compliance;
using Wolverine.Redis;

namespace Wolverine.Redis.Tests;

public class RedisTransportFixture : TransportComplianceFixture
{
    public RedisTransportFixture() : base(new Uri($"redis://localhost:6379?streamKey=wolverine-tests-{Guid.NewGuid():N}"))
    {
    }

    public async Task InitializeAsync()
    {
        await SenderIs(opts =>
        {
            opts.UseRedisTransport("localhost:6379")
                .AutoProvision();
        });

        await ReceiverIs(opts =>
        {
            opts.UseRedisTransport("localhost:6379")
                .AutoProvision();
                
            opts.ListenToRedisStream("wolverine-tests", "test-consumer-group");
        });
    }
}
