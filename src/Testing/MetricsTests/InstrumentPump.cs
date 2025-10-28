using System.Transactions;
using JasperFx;
using JasperFx.Core.Reflection;
using JasperFx.MultiTenancy;
using Wolverine.Runtime.Metrics;

namespace MetricsTests;

public class InstrumentPump
{
    private readonly string[] _tenants;

    private readonly string[] _exceptions =
    [
        typeof(BadImageFormatException).FullNameInCode(), typeof(DivideByZeroException).FullNameInCode(),
        typeof(InvalidTimeZoneException).FullNameInCode(), typeof(UnknownTenantIdException).FullNameInCode()
    ];

    public InstrumentPump(string[] tenants)
    {
        _tenants = tenants;
    }

    public List<IHandlerMetricsData> Data { get; } = new();

    public PerTenantMetrics GetExpectedForTenantId(string tenantId)
    {
        var tracking = new PerTenantTracking(tenantId);
        foreach (var data in Data.Where(x => x.TenantId == tenantId))
        {
            data.Apply(tracking);
        }

        return tracking.CompileAndReset();
    }

    public async Task Publish(int number, MessageTypeMetricsAccumulator accumulator)
    {
        Func<IHandlerMetricsData, ValueTask> publish = d =>
        {
            Data.Add(d);
            return accumulator.EntryPoint.PostAsync(d);
        };
        
        for (int i = 0; i < number; i++)
        {
            var tenantId = determineTenantId();

            var random = Random.Shared.Next(0, 10);

            if (random < 6)
            {
                var executionTime = Random.Shared.Next(50, 1000);
                var effectiveTime = executionTime + Random.Shared.NextDouble();
                await publish(new RecordExecutionTime(executionTime, tenantId));
                await publish(new RecordEffectiveTime(effectiveTime, tenantId));
            }
            else if (random <= 9)
            {
                var exceptionType = _exceptions[Random.Shared.Next(0, _exceptions.Length - 1)];
                await publish(new RecordFailure(exceptionType, tenantId));
            }
            else
            {
                var exceptionType = _exceptions[Random.Shared.Next(0, _exceptions.Length - 1)];
                await publish(new RecordDeadLetter(exceptionType, tenantId));
            }
        }
    }

    public async Task Publish(int number, string tenantId, MessageTypeMetricsAccumulator accumulator)
    {
        Func<IHandlerMetricsData, ValueTask> publish = d =>
        {
            Data.Add(d);
            return accumulator.EntryPoint.PostAsync(d);
        };
        
        for (int i = 0; i < number; i++)
        {
            var random = Random.Shared.Next(0, 10);

            if (random < 6)
            {
                var executionTime = Random.Shared.Next(50, 1000);
                var effectiveTime = executionTime + Random.Shared.NextDouble();
                await publish(new RecordExecutionTime(executionTime, tenantId));
                await publish(new RecordEffectiveTime(effectiveTime, tenantId));
            }
            else if (random <= 9)
            {
                var exceptionType = _exceptions[Random.Shared.Next(0, _exceptions.Length - 1)];
                await publish(new RecordFailure(exceptionType, tenantId));
            }
            else
            {
                var exceptionType = _exceptions[Random.Shared.Next(0, _exceptions.Length - 1)];
                await publish(new RecordDeadLetter(exceptionType, tenantId));
            }
        }
    }

    private string determineTenantId()
    {
        if (_tenants.Length <= 1) return StorageConstants.DefaultTenantId;
        
        return _tenants[Random.Shared.Next(0, _tenants.Length - 1)];
    }
}