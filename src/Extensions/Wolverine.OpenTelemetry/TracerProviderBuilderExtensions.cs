using OpenTelemetry.Trace;
using Wolverine.Runtime;
using Wolverine.Runtime.Tracing;

namespace Wolverine.OpenTelemetry;

public static class TracerProviderBuilderExtensions
{
    private static readonly string _activitySourceName = typeof(WolverineRuntime).Assembly.GetName().Name;
    public static TracerProviderBuilder AddWolverineInstrumentation(this TracerProviderBuilder builder)
        => AddWolverineInstrumentation(builder, configure: null);

    public static TracerProviderBuilder AddWolverineInstrumentation(this TracerProviderBuilder builder,
        Action<WolverineTracingOptions> configure)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder), "Must not be null");
        }

        builder.ConfigureServices(services =>
        {
            services.AddTelemetryExtension(configure);
        });
        return builder.AddSource(_activitySourceName);
    }
}