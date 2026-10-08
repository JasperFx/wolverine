using JasperFx.Core;
using Wolverine;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// GH-4863. The page query that recovers dormant inbox rows carries no ORDER BY, and none would be reliable
/// across stores and id generators, so <see cref="ListenerInboxRecovery" /> orders what it recovered by the
/// time each envelope was sent before handing it to the listener. The integration half -- a global partition
/// slot's backlog resuming on its new owner in sequence -- is pinned in PostgresqlTests; this pins the key.
/// </summary>
public class recovered_envelopes_are_replayed_in_sent_order_4863
{
    private static Envelope sentAt(DateTimeOffset at, Guid? id = null)
    {
        return new Envelope { SentAt = at, Id = id ?? Guid.NewGuid() };
    }

    [Fact]
    public void orders_by_sent_time_whatever_order_the_store_returned()
    {
        var start = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var inSentOrder = Enumerable.Range(0, 30).Select(i => sentAt(start.AddMilliseconds(i))).ToArray();

        // The reporter's shape: three interleaved runs, which is what a heap scan after the release update
        // handed back
        var scrambled = new List<Envelope>();
        for (var i = 0; i < 10; i++)
        {
            scrambled.Add(inSentOrder[i]);
            scrambled.Add(inSentOrder[i + 10]);
            scrambled.Add(inSentOrder[i + 20]);
        }

        ListenerInboxRecovery.OrderForReplay(scrambled).ShouldBe(inSentOrder);
    }

    [Fact]
    public void a_tie_on_sent_time_is_broken_by_id_so_the_order_is_deterministic()
    {
        var at = DateTimeOffset.UtcNow;
        var ids = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).OrderBy(x => x).ToArray();
        var envelopes = ids.Select(id => sentAt(at, id)).ToArray();

        var shuffled = envelopes.OrderBy(_ => Random.Shared.Next()).ToList();

        ListenerInboxRecovery.OrderForReplay(shuffled).Select(x => x.Id).ShouldBe(ids);
    }

    [Fact]
    public void already_ordered_input_is_unchanged()
    {
        var start = DateTimeOffset.UtcNow;
        var envelopes = Enumerable.Range(0, 5).Select(i => sentAt(start.AddSeconds(i))).ToList();

        ListenerInboxRecovery.OrderForReplay(envelopes).ShouldBe(envelopes);
    }
}
