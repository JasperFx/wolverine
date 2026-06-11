using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Tracking;

public class envelope_history_thread_safety
{
    [Fact]
    public async Task recording_from_concurrent_listeners_is_thread_safe()
    {
        // Reproduces a race seen with TrackActivity().IncludeExternalTransports() against a real
        // broker: multiple transport listener threads record into the same EnvelopeHistory, which
        // was backed by an unsynchronized List<T>. Concurrent Add + LINQ iteration threw
        // intermittent NullReferenceExceptions from RecordCrossApplication, failing whatever
        // tracked session happened to be active.
        var history = new EnvelopeHistory(Guid.NewGuid());

        const int writers = 8;
        const int recordsPerWriter = 10_000;

        var tasks = Enumerable.Range(0, writers).Select(_ => Task.Run(() =>
        {
            var nodeId = Guid.NewGuid();
            for (var i = 0; i < recordsPerWriter; i++)
            {
                var eventType = (i % 4) switch
                {
                    0 => MessageEventType.Sent,
                    1 => MessageEventType.Received,
                    2 => MessageEventType.ExecutionStarted,
                    _ => MessageEventType.ExecutionFinished
                };

                history.RecordCrossApplication(new EnvelopeRecord(eventType, ObjectMother.Envelope(), i, null)
                {
                    UniqueNodeId = nodeId
                });

                // Interleave the read paths TrackedSession uses while writes are in flight
                history.IsComplete();
            }
        })).ToArray();

        await Task.WhenAll(tasks);

        history.Records.Count().ShouldBe(writers * recordsPerWriter);
    }
}
