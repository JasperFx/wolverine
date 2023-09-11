using OpenTelemetry.Trace;
using Wolverine.Runtime;
using Wolverine.Runtime.Tracing;

namespace Wolverine.OpenTelemetry;
/// <summary>
/// Extension methods to simplify registering of Wolverine instrumentation.
/// </summary>
public static class TracerProviderBuilderExtensions
{
    private static readonly string _activitySourceName = typeof(WolverineRuntime).Assembly.GetName().Name;
    
    /// <summary>
    /// Enables envelope event data collection for Wolverine
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddWolverineInstrumentation(this TracerProviderBuilder builder)
        => AddWolverineInstrumentation(builder, configureWolverineTracingOptions: null);
    /// <summary>
    /// Enables envelope event data collection for Wolverine
    /// </summary>
    /// <param name="builder"><see cref="TracerProviderBuilder"/> being configured.</param>
    /// <param name="configureWolverineTracingOptions">Callback action for configuring <see cref="WolverineTracingOptions"/>.</param>
    /// <returns>The instance of <see cref="TracerProviderBuilder"/> to chain the calls.</returns>
    public static TracerProviderBuilder AddWolverineInstrumentation(this TracerProviderBuilder builder,
        Action<WolverineTracingOptions> configureWolverineTracingOptions)
    {
        if (builder is null)
        {
            throw new ArgumentNullException(nameof(builder), "Must not be null");
        }

        builder.ConfigureServices(services =>
        {
            services.AddTelemetryExtension(configureWolverineTracingOptions);
        });
        return builder.AddSource(_activitySourceName);
    }
}