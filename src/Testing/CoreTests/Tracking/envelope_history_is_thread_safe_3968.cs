using Wolverine.Tracking;
using Wolverine.Transports;
using Xunit;

namespace CoreTests.Tracking;

/// <summary>
/// GH-3968, backporting GH-3069/GH-3070 to the 5.x line. <see cref="EnvelopeHistory" /> kept its
/// <see cref="EnvelopeRecord" />s in a bare <c>List&lt;T&gt;</c> with no synchronisation, while records arrive
/// concurrently. A resize racing a read threw <c>NullReferenceException</c> out of <c>IsComplete()</c>, which
/// surfaced as an <c>AggregateException</c> from <c>TrackedSession.AssertNoExceptionsWereThrown()</c> with no
/// hint of the real cause.
/// </summary>
/// <remarks>
/// <para>GH-3069 reported it through <c>RecordCrossApplication</c> with RabbitMQ. GH-3968 confirmed the same
/// race independently on the purely LOCAL path with no external transport, reached through
/// <c>WolverineRuntime.MessageSucceeded</c> — so it races on handler-completion threads too, not only
/// transport-listener threads. Their trigger was <c>MultipleHandlerBehavior.Separated</c>: one
/// <c>ExecuteAndWaitAsync</c> fanning out to many envelopes completing at once. Reproduced roughly 1 run in 7
/// of a ~1850-test suite; ≈3% over a longer history.</para>
///
/// <para>A race that shows up 3% of the time in a full suite is no use as a regression test, so these drive
/// the contention directly and hard. All three fail on the unfixed 5.x class, on every run.</para>
/// </remarks>
public class envelope_history_is_thread_safe_3968
{
    private static EnvelopeRecord RecordOf(MessageEventType eventType)
    {
        var envelope = new Envelope
        {
            Message = new object(),
            Destination = new Uri($"{TransportConstants.Local}://one"),
            MessageType = "thing"
        };

        return new EnvelopeRecord(eventType, envelope, 0, null);
    }

    /// <summary>
    /// Writers racing readers, which is the reported shape: the resize inside <c>_records.Add</c> is what the
    /// reader trips over.
    /// </summary>
    [Fact]
    public async Task recording_while_reading_does_not_corrupt_the_history()
    {
        var history = new EnvelopeHistory(Guid.NewGuid());
        using var start = new Barrier(participantCount: 8);

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            start.SignalAndWait();

            for (var j = 0; j < 2_000; j++)
            {
                if (i % 2 == 0)
                {
                    history.RecordLocally(RecordOf(MessageEventType.Sent));
                }
                else
                {
                    // Deliberately the ENUMERATING readers. IsComplete() and Has() walk the list with an
                    // indexed for loop, and List<T> answers Count without enumerating, so all three stay
                    // quiet under concurrent mutation -- they can read a torn value but they do not throw,
                    // and a test built on them passes on the unfixed class. Message and MessageFor run
                    // LINQ over the list, which is what actually trips "Collection was modified".
                    _ = history.Message;
                    _ = history.MessageFor(MessageEventType.Sent);
                    _ = history.Records.ToArray();

                    // Kept for coverage of the indexed readers, which still need the lock to avoid
                    // reading a half-written slot even though they cannot throw
                    history.IsComplete();
                    history.Has(MessageEventType.Sent);
                }
            }
        })).ToArray();

        // No assertion beyond "nothing threw": an unsynchronised List<T> resizing under a concurrent
        // enumeration throws NullReferenceException or InvalidOperationException, which is the bug.
        await Task.WhenAll(tasks);
    }

    /// <summary>
    /// The completion path specifically. <c>MessageSucceeded</c> walks every record marking it complete while
    /// other threads are still adding, and GH-3968's stack traces land in exactly that loop.
    /// </summary>
    [Fact]
    public async Task concurrent_completions_do_not_corrupt_the_history()
    {
        var history = new EnvelopeHistory(Guid.NewGuid());
        using var start = new Barrier(participantCount: 8);

        var tasks = Enumerable.Range(0, 8).Select(i => Task.Run(() =>
        {
            start.SignalAndWait();

            for (var j = 0; j < 1_000; j++)
            {
                history.RecordLocally(RecordOf(MessageEventType.Sent));

                // Two completions in the same instant is what the report saw -- two identical inner
                // exceptions from one ExecuteAndWaitAsync
                history.RecordLocally(RecordOf(MessageEventType.MessageSucceeded));
                history.IsComplete();
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        // Every record was added, none lost to a torn write
        history.Records.Count().ShouldBe(8 * 1_000 * 2);
    }

    /// <summary>
    /// <c>Records</c> must hand out a SNAPSHOT. Returning the live list lets a caller enumerate it while a
    /// recording thread is adding, which is the same race one level removed -- and on 5.x it was
    /// <c>public IEnumerable&lt;EnvelopeRecord&gt; Records =&gt; _records;</c>.
    /// </summary>
    [Fact]
    public async Task Records_hands_out_a_snapshot_rather_than_the_live_list()
    {
        var history = new EnvelopeHistory(Guid.NewGuid());
        history.RecordLocally(RecordOf(MessageEventType.Sent));

        var snapshot = history.Records;
        var countAtCapture = snapshot.Count();

        await Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) history.RecordLocally(RecordOf(MessageEventType.Sent));
        });

        // The captured sequence is unchanged by everything added after it -- and enumerating it again here
        // would throw "Collection was modified" if it were the live list
        snapshot.Count().ShouldBe(countAtCapture);
        history.Records.Count().ShouldBe(countAtCapture + 500);
    }
}
