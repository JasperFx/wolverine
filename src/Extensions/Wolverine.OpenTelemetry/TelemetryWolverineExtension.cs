using Wolverine.Runtime.Tracing;

namespace Wolverine.OpenTelemetry;

internal class TelemetryWolverineExtension : IWolverineExtension
{
    private readonly Action<WolverineTracingOptions> _configureTelemetry;

    public TelemetryWolverineExtension(Action<WolverineTracingOptions> configureTelemetry)
    {
        _configureTelemetry = configureTelemetry;
    }
    public void Configure(WolverineOptions options)
    {
        _configureTelemetry?.Invoke(options.Tracing);
    }
}