namespace Wolverine.Runtime.Metrics;

public class PerTenantTracking
{
    public string TenantId { get; }

    public PerTenantTracking(string tenantId)
    {
        TenantId = tenantId;
    }
    
    public int Executions { get; set; }
    public long TotalExecutionTime { get; set; }
    
    public int Completions { get; set; }
    public double TotalEffectiveTime { get; set; }

    public Dictionary<string, int> DeadLetterCounts { get; } = new();
    public Dictionary<string, int> Failures { get; } = new();

    public PerTenantMetrics CompileAndReset()
    {
        var exceptionTypes = DeadLetterCounts.Keys.Union(Failures.Keys).ToArray();

        var response = new PerTenantMetrics(
            TenantId,
            new Executions(Executions, TotalExecutionTime),
            new EffectiveTime(Completions, TotalEffectiveTime),
            exceptionTypes.OrderBy(x => x).Select(exceptionType =>
            {
                int failures = 0;
                int deadLetters = 0;
                DeadLetterCounts.TryGetValue(exceptionType, out deadLetters);
                Failures.TryGetValue(exceptionType, out failures);

                return new ExceptionCounts(exceptionType, failures, deadLetters);
            }).ToArray()
        );
        
        Clear();

        return response;
    }
    
    public void Clear()
    {
        Executions = 0;
        TotalExecutionTime = 0;
        Completions = 0;
        TotalEffectiveTime = 0;
        DeadLetterCounts.Clear();
        Failures.Clear();
    }

}