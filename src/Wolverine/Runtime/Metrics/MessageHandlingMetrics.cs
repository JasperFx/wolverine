using Wolverine.Persistence.Durability.DeadLetterManagement;

namespace Wolverine.Runtime.Metrics;

public record MessageHandlingMetrics(
    int NodeNumber,
    string MessageType,
    Uri Destination,
    TimeRange Range,
    PerTenantMetrics[] PerTenant);

public record PerTenantMetrics(string TenantId, Executions Executions, EffectiveTime EffectiveTime, ExceptionCounts[] Exceptions);

public record Executions(int Count, long TotalTime);

public record EffectiveTime(int Count, double TotalTime);

public record ExceptionCounts(string ExceptionType, int Failures, int DeadLetters);
