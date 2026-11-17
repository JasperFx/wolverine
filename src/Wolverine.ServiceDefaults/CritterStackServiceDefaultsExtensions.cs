using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Wolverine.HealthChecks;
using Wolverine.Runtime;
using Wolverine.ServiceDefaults;

namespace Wolverine;

/// <summary>
/// The .NET Aspire "service defaults" for a Critter Stack service: OpenTelemetry logging, tracing and metrics for
/// every Wolverine, Marten, Polecat, Fisher and CritterWatch source and meter, exported over OTLP when an Aspire
/// AppHost (or anything else) sets <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>, plus health checks served on the
/// <c>/health</c> and <c>/alive</c> endpoints that <c>WithHttpHealthCheck()</c> in the AppHost polls.
/// </summary>
public static class CritterStackServiceDefaultsExtensions
{
    /// <summary>
    /// The readiness endpoint. Every registered health check must pass. This is the path an Aspire AppHost's
    /// <c>WithHttpHealthCheck("/health")</c> polls.
    /// </summary>
    public const string HealthEndpointPath = "/health";

    /// <summary>
    /// The liveness endpoint. Only the checks tagged <see cref="LiveTag"/> must pass.
    /// </summary>
    public const string AlivenessEndpointPath = "/alive";

    /// <summary>
    /// The tag of the health checks <see cref="AlivenessEndpointPath"/> runs.
    /// </summary>
    public const string LiveTag = "live";

    /// <summary>
    /// The tag of the Wolverine readiness checks, which only <see cref="HealthEndpointPath"/> runs.
    /// </summary>
    public const string ReadyTag = "ready";

    /// <summary>
    /// Apply the Critter Stack service defaults: <see cref="ConfigureCritterStackOpenTelemetry{TBuilder}"/> and
    /// <see cref="AddCritterStackHealthChecks{TBuilder}"/>. Map the endpoints with
    /// <see cref="MapCritterStackDefaultEndpoints"/>.
    /// </summary>
    public static TBuilder AddCritterStackServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureCritterStackOpenTelemetry();
        builder.AddCritterStackHealthChecks();

        return builder;
    }

    /// <summary>
    /// Bootstrap Wolverine for a service an Aspire AppHost orchestrates: applies
    /// <see cref="AddCritterStackServiceDefaults{TBuilder}"/>, then <c>UseWolverine(configure)</c>. Use the
    /// <c>...UsingNamedConnection("resource")</c> transport and message store overloads inside
    /// <paramref name="configure"/> to connect to the AppHost's resources, and call
    /// <see cref="MapCritterStackDefaultEndpoints"/> on the built application for <c>/health</c> and <c>/alive</c>.
    /// </summary>
    public static TBuilder UseWolverineFromAspire<TBuilder>(this TBuilder builder,
        Action<WolverineOptions>? configure = null)
        where TBuilder : IHostApplicationBuilder
    {
        builder.AddCritterStackServiceDefaults();
        builder.UseWolverine(configure);

        return builder;
    }

    /// <summary>
    /// OpenTelemetry logging, tracing and metrics for the Critter Stack (see <see cref="CritterStackTelemetry"/>),
    /// the application's own <c>ApplicationName</c> source, and ASP.NET Core and <c>HttpClient</c> instrumentation
    /// with the health endpoints filtered out of the traces. Everything is exported over OTLP when
    /// <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> is configured, which an Aspire AppHost does for every project resource
    /// so the data shows on the Aspire dashboard. Without it, nothing is exported.
    /// </summary>
    public static TBuilder ConfigureCritterStackOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        var openTelemetry = builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(CritterStackTelemetry.Meters.ToArray());
            })
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddSource(CritterStackTelemetry.ActivitySources.ToArray())
                    .AddAspNetCoreInstrumentation(options =>
                        // Keep the health check polling out of the traces
                        options.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath))
                    .AddHttpClientInstrumentation();
            });

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            openTelemetry.UseOtlpExporter();
        }

        return builder;
    }

    /// <summary>
    /// The default health checks:
    /// <list type="bullet">
    ///   <item><description><c>self</c>, tagged <see cref="LiveTag"/>: the process is responsive. The only check <c>/alive</c> runs.</description></item>
    ///   <item><description><c>wolverine</c>, tagged <see cref="ReadyTag"/>: the Wolverine runtime has started and is not shutting down.</description></item>
    ///   <item><description><c>wolverine-listeners</c>, tagged <see cref="ReadyTag"/>: Wolverine's listeners are accepting messages.</description></item>
    ///   <item><description><c>wolverine-transports</c>, tagged <see cref="ReadyTag"/>: the broker connections of every transport with a health check.</description></item>
    /// </list>
    /// The Wolverine checks report Healthy with a note in an application that does not use Wolverine, so the same
    /// defaults serve a Marten, Polecat or Fisher service on its own.
    /// </summary>
    public static TBuilder AddCritterStackHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        WolverineTransportsHealthCheck? transports = null;

        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), [LiveTag])
            .Add(wolverineCheck("wolverine", runtime => new WolverineBusHealthCheck(runtime)))
            .Add(wolverineCheck("wolverine-listeners", runtime => new WolverineListenerHealthCheck(runtime)))
            // One instance for the application's lifetime, so each transport's health check is built only once
            .Add(wolverineCheck("wolverine-transports",
                runtime => transports ??= new WolverineTransportsHealthCheck(runtime)));

        return builder;
    }

    /// <summary>
    /// Map <see cref="HealthEndpointPath"/> (every health check must pass for the service to be ready) and
    /// <see cref="AlivenessEndpointPath"/> (only the checks tagged <see cref="LiveTag"/>). Point the Aspire AppHost
    /// at the first with <c>.WithHttpHealthCheck("/health")</c>.
    /// </summary>
    public static TEndpoints MapCritterStackDefaultEndpoints<TEndpoints>(this TEndpoints endpoints)
        where TEndpoints : IEndpointRouteBuilder
    {
        endpoints.MapHealthChecks(HealthEndpointPath);

        endpoints.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(LiveTag)
        });

        return endpoints;
    }

    private static HealthCheckRegistration wolverineCheck(string name, Func<IWolverineRuntime, IHealthCheck> factory)
    {
        return new HealthCheckRegistration(name,
            sp => sp.GetService<IWolverineRuntime>() is { } runtime ? factory(runtime) : NoWolverineHealthCheck.Instance,
            failureStatus: null,
            tags: [ReadyTag]);
    }

    private sealed class NoWolverineHealthCheck : IHealthCheck
    {
        public static readonly NoWolverineHealthCheck Instance = new();

        public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Wolverine is not configured in this application."));
        }
    }
}
