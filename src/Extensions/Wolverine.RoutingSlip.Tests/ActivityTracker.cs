using System.Collections.Concurrent;

namespace Wolverine.RoutingSlip.Tests;

internal static class ActivityTracker
{
    private static readonly ConcurrentDictionary<Guid, List<ActivityExecutionRecord>> Executions = new();
    private static readonly ConcurrentDictionary<Guid, List<ActivityCompensationRecord>> Compensations = new();

    public static void RecordExecution(Guid trackingNumber, string activityName, Uri destination)
    {
        var records = Executions.GetOrAdd(trackingNumber, _ => []);
        lock (records)
        {
            var attempt = records.Count(r => r.ActivityName == activityName) + 1;
            records.Add(new ActivityExecutionRecord(activityName, destination, attempt));
        }
    }

    public static void RecordCompensation(Guid trackingNumber, string activityName, Uri destination)
    {
        var records = Compensations.GetOrAdd(trackingNumber, _ => []);
        lock (records)
        {
            records.Add(new ActivityCompensationRecord(activityName, destination));
        }
    }

    public static IReadOnlyList<ActivityExecutionRecord> GetExecutions(Guid trackingNumber)
    {
        return Executions.TryGetValue(trackingNumber, out var records)
            ? records.ToList()
            : Array.Empty<ActivityExecutionRecord>();
    }

    public static IReadOnlyList<ActivityCompensationRecord> GetCompensations(Guid trackingNumber)
    {
        return Compensations.TryGetValue(trackingNumber, out var records)
            ? records.ToList()
            : Array.Empty<ActivityCompensationRecord>();
    }

    public static void Reset()
    {
        Executions.Clear();
        Compensations.Clear();
    }
}

internal record ActivityExecutionRecord(string ActivityName, Uri Destination, int Attempt);

internal record ActivityCompensationRecord(string ActivityName, Uri Destination);
