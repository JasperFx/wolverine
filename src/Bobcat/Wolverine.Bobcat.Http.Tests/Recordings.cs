using Bobcat;

namespace Wolverine.Bobcat.Http.Tests;

/// <summary>
/// Opens a Bobcat recording around a block of steps, so a test can read back exactly what the
/// specification rendered: each step's keyword, text, cells and verdict.
/// </summary>
public static class Recordings
{
    public static async Task<ScenarioRecorder.Recording> RecordAsync(Func<Task> steps)
    {
        var recording = ScenarioRecorder.Begin("Wolverine.Bobcat", Guid.NewGuid().ToString(), null, Guid.NewGuid());
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

    public static ScenarioRecorder.RecordedStep Step(this ScenarioRecorder.Recording recording, string text)
        => recording.Steps.Single(x => x.Text == text);
}
