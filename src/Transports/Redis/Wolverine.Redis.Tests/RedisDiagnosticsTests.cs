using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using StackExchange.Redis;
using Shouldly;
using Wolverine.Redis.Internal;
using Xunit;

namespace Wolverine.Redis.Tests;

public class RedisDiagnosticsTests
{
    private static async Task<IDatabase> ConnectAsync() => (await ConnectionMultiplexer.ConnectAsync("localhost:6379")).GetDatabase();

    [Fact]
    public async Task get_attributes_and_purge()
    {
        var streamKey = $"wolverine-tests-diag-{Guid.NewGuid():N}";
        var transport = new RedisTransport("localhost:6379");
        var endpoint = transport.StreamEndpoint(streamKey);

        var db = await ConnectAsync();

        // push a couple messages
        for (int i = 0; i < 3; i++)
        {
            var payload = Encoding.UTF8.GetBytes("{}");
            await db.StreamAddAsync(streamKey, new[]
            {
                new NameValueEntry("payload", (ReadOnlyMemory<byte>)payload),
                new NameValueEntry("wolverine-message-type", "noop"),
            });
        }

        var attrs = await endpoint.GetAttributesAsync();
        attrs["streamKey"].ShouldBe(streamKey);
        attrs.ContainsKey("messageCount").ShouldBeTrue();
        attrs["messageCount"].ShouldBe("3");

        // Purge
        await endpoint.PurgeAsync(NullLogger.Instance);
        var len = await db.StreamLengthAsync(streamKey);
        len.ShouldBe(0);
    }
}

