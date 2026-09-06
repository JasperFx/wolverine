using System.Collections.Concurrent;
using Wolverine.ComplianceTests;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.Tracking;
using Xunit;

namespace PersistenceTests.Durability;

/// <summary>
/// GH-4319. The durable local queue used to pay one connection and one INSERT per published message.
/// It now coalesces concurrent publishes into one batched insert. These tests exist to prove that
/// batching changed the number of round trips and nothing else: the same messages land, exactly once,
/// and a duplicate in the middle of a burst still fails only itself.
/// </summary>
[Collection("marten")]
public class coalesced_local_queue_storage_4319 : PostgresqlContext, IAsyncLifetime
{
    private const string TheSchema = "coalesced_4319";

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static Task<IHost> hostAsync(int batchSize)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = "Coalesced4319";
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, TheSchema);
                opts.Policies.UseDurableLocalQueues();
                opts.Durability.StoreIncomingBatchSize = batchSize;

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);
            }).StartAsync();
    }

    /// <summary>
    /// The batch size is the ONLY difference between these two runs. Coalesced or not, every message
    /// published in the burst has to be handled exactly once -- that is the whole safety claim.
    /// </summary>
    [Theory]
    [InlineData(1)]     // coalescing off: one round trip per publish, the pre-GH-4319 shape
    [InlineData(100)]   // the shipping default
    public async Task a_concurrent_burst_is_persisted_and_handled_exactly_once(int batchSize)
    {
        CoalesceTarget.Received.Clear();

        using var host = await hostAsync(batchSize);

        var messages = Enumerable.Range(0, 200).Select(i => new CoalesceMessage(i)).ToArray();

        await host.TrackActivity()
            .Timeout(60.Seconds())
            .ExecuteAndWaitAsync(publishAll);

        // Publish them all at once from many threads. Concurrency is the only thing that forms a
        // batch -- there is deliberately no timer to wait on.
        Task publishAll(IMessageContext context) =>
            Parallel.ForEachAsync(messages, TestContext.Current.CancellationToken,
                async (message, _) => await context.PublishAsync(message));

        CoalesceTarget.Received.Count.ShouldBe(messages.Length);
        CoalesceTarget.Received.OrderBy(x => x).ShouldBe(messages.Select(x => x.Number).OrderBy(x => x));
    }

    /// <summary>
    /// The contract the coalescer's fallback rests on. A batched insert that hits a duplicate key
    /// rolls the whole batch back and reports which identities were already present -- so the
    /// coalescer can safely retry each envelope on its own and let only the duplicate fail. If this
    /// ever became a partial write, the coalesced local queue would double-insert on retry.
    /// </summary>
    [Fact]
    public async Task a_failed_batch_insert_rolls_back_whole_and_names_the_duplicates()
    {
        using var host = await hostAsync(100);
        var inbox = host.GetRuntime().Storage.Inbox;

        var alreadyThere = ObjectMother.Envelope();
        alreadyThere.Status = EnvelopeStatus.Incoming;
        await inbox.StoreIncomingAsync(alreadyThere);

        var fresh = Enumerable.Range(0, 5).Select(_ =>
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;
            return envelope;
        }).ToArray();

        var batch = fresh.Append(alreadyThere).ToArray();

        var ex = await Should.ThrowAsync<DuplicateIncomingEnvelopeException>(
            () => inbox.StoreIncomingAsync(batch));

        // Pinpointed, not "the whole batch might be duplicates": the coalescer's fallback is only
        // safe because the store can tell exactly which identity collided
        ex.Duplicates.ShouldHaveSingleItem().Id.ShouldBe(alreadyThere.Id);

        // Rolled back whole: not one of the fresh envelopes was left behind, which is what makes the
        // per-envelope retry safe rather than a source of duplicates
        foreach (var envelope in fresh)
        {
            (await inbox.ExistsAsync(envelope, TestContext.Current.CancellationToken))
                .ShouldBeFalse($"Envelope {envelope.Id} survived a rolled-back batch");
        }
    }
}

public record CoalesceMessage(int Number);

public static class CoalesceTarget
{
    public static readonly ConcurrentBag<int> Received = new();
}

public static class CoalesceMessageHandler
{
    public static void Handle(CoalesceMessage message) => CoalesceTarget.Received.Add(message.Number);
}
