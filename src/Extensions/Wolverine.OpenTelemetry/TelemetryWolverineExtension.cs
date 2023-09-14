using Wolverine.Runtime.Tracing;

namespace Wolverine.OpenTelemetry;

internal class TelemetryWolverineExtension : IWolverineExtension
{
    private readonly Action<WolverineTracingOptions> _configureWolverineTracingOptions;

    public TelemetryWolverineExtension(Action<WolverineTracingOptions> configureWolverineTracingOptions)
    {
        _configureWolverineTracingOptions = configureWolverineTracingOptions;
    }
    public void Configure(WolverineOptions options)
    {
        _configureWolverineTracingOptions?.Invoke(options.Tracing);
    }
}