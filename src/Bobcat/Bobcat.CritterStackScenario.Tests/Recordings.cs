using Bobcat.Monitoring;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// Opens a Bobcat recording around a block of steps, so a test can read back exactly what the specification
/// rendered — each step's keyword, text, cells and verdict — and what reached the monitor wire.
/// </summary>
public static class Recordings
{
    public static async Task<ScenarioRecorder.Recording> RecordAsync(Func<Task> steps, IMonitorEventSink? sink = null)
    {
        var recording = ScenarioRecorder.Begin("CritterStackScenario", Guid.NewGuid().ToString(), sink, Guid.NewGuid());
        try
        {
            await steps();
        }
        finally
        {
            recording.Dispose();
        }

        return recording;
    }

    public static IEnumerable<string> Lines(this ScenarioRecorder.Recording recording)
        => recording.Steps.Select(x => $"{x.Keyword} {x.Text}");

    public static ScenarioRecorder.RecordedStep Step(this ScenarioRecorder.Recording recording, string text)
        => recording.Steps.Single(x => x.Text == text);
}

/// <summary>Every monitor event a scenario posted, in order.</summary>
public sealed class RecordingSink : IMonitorEventSink
{
    private readonly List<MonitorEvent> _events = new();

    public void Post(MonitorEvent @event)
    {
        lock (_events) _events.Add(@event);
    }

    public IReadOnlyList<MonitorEvent> Events
    {
        get
        {
            lock (_events) return _events.ToArray();
        }
    }
}
