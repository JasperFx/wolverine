using System.Collections.Concurrent;

namespace Wolverine.RoutingSlip.Tests;

internal static class ActivityTracker
{
    private static readonly ConcurrentDictionary<Guid, List<ActivityExecutionRecord>> Executions = new();
    private static readonly ConcurrentDictionary<Guid, List<ActivityCompensationRecord>> Compensations = new();
    private static readonly ConcurrentDictionary<Guid, List<ExceptionInfo>> ActivityFaults = new();
    private static readonly ConcurrentDictionary<Guid, List<ExceptionInfo>> CompensationFailures = new();

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
        if (!Executions.TryGetValue(trackingNumber, out var records))
        {
            return Array.Empty<ActivityExecutionRecord>();
        }

        lock (records)
        {
            return records.ToList();
        }
    }

    public static IReadOnlyList<ActivityCompensationRecord> GetCompensations(Guid trackingNumber)
    {
        if (!Compensations.TryGetValue(trackingNumber, out var records))
        {
            return Array.Empty<ActivityCompensationRecord>();
        }

        lock (records)
        {
            return records.ToList();
        }
    }

    public static void RecordActivityFault(Guid trackingNumber, ExceptionInfo exceptionInfo)
    {
        var records = ActivityFaults.GetOrAdd(trackingNumber, _ => []);
        lock (records)
        {
            records.Add(exceptionInfo);
        }
    }

    public static void RecordCompensationFailure(Guid trackingNumber, ExceptionInfo exceptionInfo)
    {
        var records = CompensationFailures.GetOrAdd(trackingNumber, _ => []);
        lock (records)
        {
            records.Add(exceptionInfo);
        }
    }

    public static IReadOnlyList<ExceptionInfo> GetActivityFaults(Guid trackingNumber)
    {
        if (!ActivityFaults.TryGetValue(trackingNumber, out var records))
        {
            return Array.Empty<ExceptionInfo>();
        }

        lock (records)
        {
            return records.ToList();
        }
    }

    public static IReadOnlyList<ExceptionInfo> GetCompensationFailures(Guid trackingNumber)
    {
        if (!CompensationFailures.TryGetValue(trackingNumber, out var records))
        {
            return Array.Empty<ExceptionInfo>();
        }

        lock (records)
        {
            return records.ToList();
        }
    }

    public static void Reset()
    {
        Executions.Clear();
        Compensations.Clear();
        ActivityFaults.Clear();
        CompensationFailures.Clear();
    }
}

internal record ActivityExecutionRecord(string ActivityName, Uri Destination, int Attempt);

internal record ActivityCompensationRecord(string ActivityName, Uri Destination);
