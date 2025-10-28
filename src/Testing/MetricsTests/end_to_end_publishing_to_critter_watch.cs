using JasperFx.Core;
using Newtonsoft.Json;
using Shouldly;
using Wolverine;
using Xunit.Abstractions;

namespace MetricsTests;

public class end_to_end_publishing_to_critter_watch
{
    private readonly ITestOutputHelper _output;

    public end_to_end_publishing_to_critter_watch(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public async Task run_for_quite_awhile_in_critter_watch_mode()
    {
        await using var pump = new MessagePump();

        await pump.PumpMessagesAsync(WolverineMetricsMode.CritterWatch,30.Seconds());

        var metrics = MetricsCollectionHandler.Collected;
        
        metrics.Length.ShouldBeGreaterThan(0);

        foreach (var metric in metrics)
        {
            _output.WriteLine(JsonConvert.SerializeObject(metric, Formatting.Indented));
            
            // metric.PerTenant[0].Executions.Count.ShouldBeGreaterThan(0);
            // metric.PerTenant[0].Executions.TotalTime.ShouldBeGreaterThan(0);
            // metric.PerTenant[0].EffectiveTime.Count.ShouldBeGreaterThan(0);
            // metric.PerTenant[0].EffectiveTime.TotalTime.ShouldBeGreaterThan(0);
            
            
        }
    }
    
    [Fact]
    public async Task run_for_quite_awhile_in_hybrid_mode()
    {
        await using var pump = new MessagePump();

        await pump.PumpMessagesAsync(WolverineMetricsMode.Hybrid,30.Seconds());

        var metrics = MetricsCollectionHandler.Collected;
        
        metrics.Length.ShouldBeGreaterThan(0);

        foreach (var metric in metrics)
        {
            _output.WriteLine(JsonConvert.SerializeObject(metric, Formatting.Indented));
            
            // metric.PerTenant[0].Executions.Count.ShouldBeGreaterThan(0);
            // metric.PerTenant[0].Executions.TotalTime.ShouldBeGreaterThan(0);
            // metric.PerTenant[0].EffectiveTime.Count.ShouldBeGreaterThan(0);
            // metric.PerTenant[0].EffectiveTime.TotalTime.ShouldBeGreaterThan(0);
            
            
        }
    }
}