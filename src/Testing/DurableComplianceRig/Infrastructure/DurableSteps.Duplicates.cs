using System.Collections.Concurrent;
using Bobcat;
using Wolverine;
using Wolverine.Logging;
using Wolverine.Tracking;
using Wolverine.Transports;

namespace DurableComplianceRig.Infrastructure;

/// <summary>Steps for two copies of one message arriving together (GH-5123).</summary>
public sealed partial class DurableSteps
{
    private readonly ConcurrentQueue<string> _listenerStops = new();

    /// <summary>
    /// GH-5123. After a broker outage the broker redelivers what it buffered while the outbox resends what it never saw
    /// confirmed, so two copies with the same envelope id arrive back to back -- close enough to share one
    /// micro-batch at a durable listener.
    /// </summary>
    [When("each message is sent twice, back to back, from node {node}")]
    public async Task EachMessageIsSentTwice(int node)
    {
        var host = Node(node);
        host.GetRuntime().Tracker.Subscribe(new ListenerStopRecorder(QueueName, _listenerStops));

        var bus = host.MessageBus();
        foreach (var id in _messages)
        {
            // RigIds.Stamp gives both copies the message's id: to the receiver the second one is a redelivery
            await bus.PublishAsync(new RigMessage(id));
            await bus.PublishAsync(new RigMessage(id));
        }
    }

    [Then("the listener on node {node} never stopped")]
    public void TheListenerNeverStopped(int node)
    {
        SpecAssert.Check($"times the listener on node {node} stopped or paused",
            _listenerStops.IsEmpty ? "none" : string.Join(", ", _listenerStops), "none");
    }

    private sealed class ListenerStopRecorder(string queueName, ConcurrentQueue<string> stops) : IObserver<IWolverineEvent>
    {
        public void OnNext(IWolverineEvent value)
        {
            if (value is ListenerState state && state.Status != ListeningStatus.Accepting &&
                state.Uri.ToString().Contains(queueName))
            {
                stops.Enqueue($"{state.Uri} went {state.Status}");
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
