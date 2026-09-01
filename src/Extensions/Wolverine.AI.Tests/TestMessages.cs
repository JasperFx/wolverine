using Wolverine.AI;
using Wolverine.Persistence;

namespace Wolverine.AI.Tests;

public record AlertRaised(string IncidentId, string Summary);

public record IncidentTriage(string Severity, string RecommendedAction);

public record IncidentSnapshot(string IncidentId, string Summary, int MinutesOpen);

public static class AlertRaisedHandler
{
    public static LlmCallout Handle(AlertRaised message)
    {
        return LlmCallout
            .Ask<IncidentTriage>("Triage this incident.",
                new IncidentSnapshot(message.IncidentId, message.Summary, 12))
            .Tagged("triage");
    }
}

/// <summary>
/// Records what the application actually received, so the tests assert on the far side of the callout
/// rather than on the executor's return value.
/// </summary>
public static class TriageResults
{
    private static readonly List<object> _received = new();

    public static void Record(object message)
    {
        lock (_received) _received.Add(message);
    }

    public static IReadOnlyList<object> Received
    {
        get
        {
            lock (_received) return _received.ToArray();
        }
    }

    public static void Clear()
    {
        lock (_received) _received.Clear();
    }
}

public static class IncidentTriageHandler
{
    public static void Handle(IncidentTriage triage)
    {
        TriageResults.Record(triage);
    }
}

public static class LlmTextResponseHandler
{
    public static void Handle(LlmTextResponse response)
    {
        TriageResults.Record(response);
    }
}
