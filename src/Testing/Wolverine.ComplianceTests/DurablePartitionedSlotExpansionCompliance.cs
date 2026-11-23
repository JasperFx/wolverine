using System.Collections.Concurrent;
using JasperFx.CodeGeneration;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ErrorHandling;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;
using Wolverine.Runtime.Partitioning;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Util;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5695. A durable slot of a partitioned topology expands a message with several handler types into one copy per
/// handler type, stores the copies as ordinary inbox rows (their ids derived from the original's id and the handler
/// type), marks the original handled, and then runs the copies one after the other. A crash between those two writes
/// leaves the original Incoming beside its copies; expanding the recovered original again finds every copy already
/// there, so no handler type runs twice
/// </summary>
public abstract class DurablePartitionedSlotExpansionCompliance : IAsyncLifetime
{
    private IHost? _host;

    /// <summary>
    /// Persist messages with the store under test, in a schema of its own
    /// </summary>
    protected abstract void PersistMessages(WolverineOptions opts);

    public ValueTask InitializeAsync()
    {
        DurableSlotLog.Reset();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_host != null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    private async Task<IHost> startHostAsync(MessageIdentity identity = MessageIdentity.IdOnly,
        Action<WolverineOptions>? configure = null)
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.MessageIdentity = identity;
                opts.Durability.ScheduledJobPollingTime = 250.Milliseconds();

                PersistMessages(opts);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(FirstDurableSlotHandler))
                    .IncludeType(typeof(SecondDurableSlotHandler));
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;

                opts.MessagePartitioning.ByMessage<DurableSlotted>(x => x.GroupId);
                opts.MessagePartitioning.PublishToPartitionedLocalMessaging("durableslots", 3, topology =>
                {
                    topology.Message<DurableSlotted>();
                    topology.ConfigureQueues(q => q.UseDurableInbox());
                });

                configure?.Invoke(opts);
            }).StartAsync();

        await _host.ResetResourceState();

        return _host;
    }

    private static async Task waitForAsync(Func<bool> condition, string description)
    {
        var timeout = DateTimeOffset.UtcNow.Add(30.Seconds());
        while (!condition() && DateTimeOffset.UtcNow < timeout)
        {
            await Task.Delay(50);
        }

        condition().ShouldBeTrue(description);
    }

    private static string[] handlerOrder(IHost host)
    {
        return host.GetRuntime().Handlers.GroupFor(typeof(DurableSlotted))!.Chains
            .Select(x => MessageHandlerGroup.HandlerTypeOf(x).Name).ToArray();
    }

    private static async Task<Dictionary<Guid, EnvelopeStatus>> incomingStatusesAsync(IHost host)
    {
        var rows = await host.GetRuntime().Storage.Admin.AllIncomingAsync();
        return rows.GroupBy(x => x.Id).ToDictionary(x => x.Key, x => x.First().Status);
    }

    private static async Task waitForHandledRowsAsync(IHost host, Guid[] ids)
    {
        var timeout = DateTimeOffset.UtcNow.Add(30.Seconds());
        Dictionary<Guid, EnvelopeStatus> statuses;
        do
        {
            statuses = await incomingStatusesAsync(host);
            if (ids.All(id => statuses.TryGetValue(id, out var status) && status == EnvelopeStatus.Handled)) return;
            await Task.Delay(100);
        } while (DateTimeOffset.UtcNow < timeout);

        throw new ShouldAssertException("Expected every one of these inbox rows to be Handled: " +
                                        ids.Select(id => $"{id}={(statuses.TryGetValue(id, out var s) ? s.ToString() : "missing")}").Join(", "));
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly)]
    [InlineData(MessageIdentity.IdAndDestination)]
    public async Task every_handler_type_runs_in_order_and_every_row_ends_handled(MessageIdentity identity)
    {
        var host = await startHostAsync(identity);

        var groups = new[] { "a", "b", "c" };
        const int perGroup = 5;

        await Task.WhenAll(groups.Select(group => Task.Run(async () =>
        {
            var bus = host.MessageBus();
            for (var i = 1; i <= perGroup; i++)
            {
                await bus.PublishAsync(new DurableSlotted(group, i));
            }
        })));

        await waitForAsync(() => DurableSlotLog.Count >= groups.Length * perGroup * 2, "every copy ran");

        var handlers = handlerOrder(host);
        foreach (var group in groups)
        {
            DurableSlotLog.For(group).ShouldBe(Enumerable.Range(1, perGroup)
                .SelectMany(i => handlers.Select(h => $"{h}:{i}")).ToArray());
        }

        // The original and both of its copies have inbox rows of their own, and all of them end handled
        var copies = DurableSlotLog.Entries();
        copies.Select(x => x.EnvelopeId).Distinct().Count().ShouldBe(copies.Length);

        var originals = copies.Select(x => Guid.Parse(x.ParentId!)).Distinct().ToArray();
        originals.Length.ShouldBe(groups.Length * perGroup);

        await waitForHandledRowsAsync(host, originals.Concat(copies.Select(x => x.EnvelopeId)).ToArray());
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly)]
    [InlineData(MessageIdentity.IdAndDestination)]
    public async Task a_crash_after_the_copies_were_stored_runs_each_handler_type_once(MessageIdentity identity)
    {
        var host = await startHostAsync(identity);

        // The state a crash between the two writes leaves behind: the original still Incoming, beside both copies.
        // Both are owned by no node, as recovery from a dead node leaves them
        var (original, copies) = expansionOf(host, new DurableSlotted("crashed", 1));
        await seedAsync(host, [original, ..copies]);

        await waitForAsync(() => DurableSlotLog.For("crashed").Length >= 2, "both copies ran");
        await waitForHandledRowsAsync(host, [original.Id, ..copies.Select(x => x.Id)]);

        // Give a second run of anything the chance to show up
        await Task.Delay(1.Seconds());

        DurableSlotLog.For("crashed").OrderBy(x => x)
            .ShouldBe(handlerOrder(host).Select(h => $"{h}:1").OrderBy(x => x).ToArray());
    }

    [Theory]
    [InlineData(MessageIdentity.IdOnly)]
    [InlineData(MessageIdentity.IdAndDestination)]
    public async Task a_crash_after_only_some_copies_were_stored_runs_each_handler_type_once(MessageIdentity identity)
    {
        var host = await startHostAsync(identity);

        // A store that doesn't write a batch atomically could leave only some of the copies behind
        var (original, copies) = expansionOf(host, new DurableSlotted("partial", 1));
        await seedAsync(host, [original, copies[0]]);

        await waitForAsync(() => DurableSlotLog.For("partial").Length >= 2, "both copies ran");
        await waitForHandledRowsAsync(host, [original.Id, ..copies.Select(x => x.Id)]);
        await Task.Delay(1.Seconds());

        DurableSlotLog.For("partial").OrderBy(x => x)
            .ShouldBe(handlerOrder(host).Select(h => $"{h}:1").OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task a_recovered_original_that_was_never_expanded_is_expanded()
    {
        var host = await startHostAsync();

        var (original, copies) = expansionOf(host, new DurableSlotted("recovered", 1));
        await seedAsync(host, [original]);

        await waitForAsync(() => DurableSlotLog.For("recovered").Length >= 2, "both copies ran");
        await waitForHandledRowsAsync(host, [original.Id, ..copies.Select(x => x.Id)]);

        DurableSlotLog.For("recovered").ShouldBe(handlerOrder(host).Select(h => $"{h}:1").ToArray());
    }

    [Fact]
    public async Task a_dead_lettered_copy_is_the_only_dead_letter_and_the_next_copy_still_runs()
    {
        FirstDurableSlotHandler.AlwaysFail = 1;

        var host = await startHostAsync(configure: opts =>
            opts.OnException<DurableSlotFailure>().MoveToErrorQueue());

        await host.MessageBus().PublishAsync(new DurableSlotted("dead", 1));
        await host.MessageBus().PublishAsync(new DurableSlotted("dead", 2));

        await waitForAsync(() => DurableSlotLog.For("dead").Length >= 4, "the rest of the group ran");

        DurableSlotLog.For("dead").ShouldBe([
            $"{nameof(FirstDurableSlotHandler)}:1:failed",
            $"{nameof(SecondDurableSlotHandler)}:1",
            $"{nameof(FirstDurableSlotHandler)}:2",
            $"{nameof(SecondDurableSlotHandler)}:2"
        ]);

        var failed = DurableSlotLog.Entries().Single(x => x.Entry.EndsWith(":failed"));
        var original = Guid.Parse(failed.ParentId!);

        var store = host.GetRuntime().Storage;
        DeadLetterEnvelope? deadLetter = null;
        await waitForAsync(() =>
        {
            deadLetter = store.DeadLetters.DeadLetterEnvelopeByIdAsync(failed.EnvelopeId).GetAwaiter().GetResult();
            return deadLetter != null;
        }, "the failed copy was dead lettered");

        deadLetter!.Envelope.Headers[EnvelopeConstants.PartitionedHandlerTypeKey]
            .ShouldBe(typeof(FirstDurableSlotHandler).FullName);

        (await store.DeadLetters.DeadLetterEnvelopeByIdAsync(original)).ShouldBeNull();
        (await incomingStatusesAsync(host))[original].ShouldBe(EnvelopeStatus.Handled);
    }

    private static (Envelope Original, Envelope[] Copies) expansionOf(IHost host, DurableSlotted message)
    {
        var runtime = host.GetRuntime();
        var serializer = runtime.Options.DefaultSerializer!;
        var slot = runtime.Options.MessagePartitioning.ShardedMessageTopologies.Single()
            .Slots[0].Uri;

        var original = new Envelope(message)
        {
            Id = Guid.NewGuid(),
            Destination = slot,
            Status = EnvelopeStatus.Incoming,
            OwnerId = TransportConstants.AnyNode,
            ContentType = serializer.ContentType,
            MessageType = typeof(DurableSlotted).ToMessageTypeName(),
            GroupId = message.GroupId,
            SentAt = DateTimeOffset.UtcNow
        };
        original.Data = serializer.Write(original);

        var copies = runtime.Handlers.GroupFor(typeof(DurableSlotted))!.Chains
            .Select(chain => PartitionedSlotExpansion.CopyFor(original, chain))
            .ToArray();

        return (original, copies);
    }

    private static Task seedAsync(IHost host, Envelope[] envelopes)
    {
        return host.GetRuntime().Storage.Inbox.StoreIncomingAsync(envelopes);
    }
}

public record DurableSlotted(string GroupId, int Sequence);

public class DurableSlotFailure : Exception;

public static class DurableSlotLog
{
    public record LogEntry(string Group, string Entry, Guid EnvelopeId, string? ParentId);

    private static readonly ConcurrentQueue<LogEntry> _entries = new();

    public static int Count => _entries.Count(x => !x.Entry.EndsWith(":failed"));

    public static void Reset()
    {
        _entries.Clear();
        FirstDurableSlotHandler.AlwaysFail = 0;
    }

    public static void Record(string handler, DurableSlotted message, Envelope envelope, string? suffix = null)
    {
        _entries.Enqueue(new LogEntry(message.GroupId,
            $"{handler}:{message.Sequence}" + (suffix == null ? "" : ":" + suffix), envelope.Id, envelope.ParentId));
    }

    public static string[] For(string group) => _entries.Where(x => x.Group == group).Select(x => x.Entry).ToArray();

    public static LogEntry[] Entries() => _entries.ToArray();
}

public class FirstDurableSlotHandler
{
    public static int AlwaysFail { get; set; }

    public async Task Handle(DurableSlotted message, Envelope envelope)
    {
        await Task.Delay(Random.Shared.Next(0, 3));

        if (message.Sequence == AlwaysFail && message.GroupId == "dead")
        {
            DurableSlotLog.Record(nameof(FirstDurableSlotHandler), message, envelope, "failed");
            throw new DurableSlotFailure();
        }

        DurableSlotLog.Record(nameof(FirstDurableSlotHandler), message, envelope);
    }
}

public class SecondDurableSlotHandler
{
    public async Task Handle(DurableSlotted message, Envelope envelope)
    {
        await Task.Delay(Random.Shared.Next(0, 3));
        DurableSlotLog.Record(nameof(SecondDurableSlotHandler), message, envelope);
    }
}
