using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using Wolverine.ComplianceTests;
using Wolverine.Postgresql;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.Redis.Tests;

/// <summary>
/// GH-4035. The Redis stream endpoint's storage -- the stream itself plus its scheduled sorted set --
/// is part of the footprint <see cref="Wolverine.Runtime.StorageExtensions.ClearAllWolverineStorageAsync"/>
/// resets, and there was no coverage of that. GH-4028 removed <c>IDatabaseBackedEndpoint</c> from the
/// endpoint for good reasons, which silently dropped Redis out of the reset because that marker doubled
/// as the selector; 38/38 checks stayed green. This suite is what would have failed.
/// </summary>
[Collection("ClearAllWolverineStorageRedis4035")]
public class clear_all_wolverine_storage : ClearAllWolverineStorageCompliance
{
    private readonly string _streamKey = $"reset-{Guid.NewGuid():N}";

    protected override void ConfigureStorage(WolverineOptions options)
    {
        options.UseRedisTransport(RedisContainerFixture.ConnectionString).AutoProvision();
        options.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "redis_reset_4035");

        // Subscriber only -- a listener would drain the stream out from under the assertions before
        // the reset ever runs. Named so the compliance suite's endpoint lookup finds it.
        options.PublishAllMessages().ToRedisStream(_streamKey).Named(QueueName);
    }

    /// <summary>
    /// A Redis XADD recreates a deleted stream key silently, so there is no missing "table" to observe
    /// after TeardownAsync(). Only the empties-it half of the rebuild scenario means anything here.
    /// </summary>
    protected override bool TeardownMakesTheQueueUnwritable => false;

    /// <summary>
    /// RedisStreamEndpoint.GetAttributesAsync() reports streamKey/messageCount/consumerGroup rather than
    /// the database queues' Count/Scheduled, so read the two keys directly instead of reshaping a
    /// diagnostic surface other things depend on.
    /// </summary>
    protected override async Task<(long Queued, long Scheduled)> queueCountsAsync()
    {
        var endpoint = (RedisStreamEndpoint)theQueue;
        var transport = theHost.GetRuntime().Options.Transports.GetOrCreate<RedisTransport>();
        var database = transport.GetDatabase(database: endpoint.DatabaseId);

        var queued = await database.KeyExistsAsync(endpoint.StreamKey)
            ? await database.StreamLengthAsync(endpoint.StreamKey)
            : 0L;

        var scheduled = await database.SortedSetLengthAsync(endpoint.ScheduledMessagesKey);

        return (queued, scheduled);
    }

    protected override ValueTask sendToQueueAsync(Envelope envelope)
    {
        var endpoint = (RedisStreamEndpoint)theQueue;
        var runtime = theHost.GetRuntime();
        var transport = runtime.Options.Transports.GetOrCreate<RedisTransport>();

        // The endpoint has no SendAsync(Envelope) of its own; the inline sender is the seam that puts
        // an immediate message on the stream and a scheduled one in the sorted set.
        return new InlineRedisStreamSender(transport, endpoint, runtime).SendAsync(envelope);
    }
}

[CollectionDefinition("ClearAllWolverineStorageRedis4035", DisableParallelization = true)]
public class ClearAllWolverineStorageRedis4035Collection;
