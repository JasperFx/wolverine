using Microsoft.Extensions.DependencyInjection;
using Wolverine.Runtime.Tracing;

namespace Wolverine.OpenTelemetry;

internal static class OpenTelemetryInstrumentationServiceCollectionExtensions
{
    public static void AddTelemetryExtension(this IServiceCollection services, Action<WolverineTracingOptions> configureWolverineTracingOptions)
    {
        checkConfigMarker(services);
        var telemetryExtension = new TelemetryWolverineExtension(configureWolverineTracingOptions);
        services.Add(new ServiceDescriptor(typeof(IWolverineExtension), telemetryExtension));
    }

    private static void checkConfigMarker(IServiceCollection services)
    {
        var marker = services.FirstOrDefault(s => s.ServiceType == typeof(TelemetryExtensionConfigMarker));
        if (marker == null)
        {
            services.AddSingleton(new TelemetryExtensionConfigMarker());
            return;
        }

        throw new InvalidOperationException(
            "Wolverine instrumentation has already been registered and doesn't support multiple registrations.");
    }
}