using JasperFx.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace Wolverine.Http;

/// <summary>
///     GH-5016. The JSON response from the durability pass endpoint
/// </summary>
public class DurabilityPassResponse
{
    public DateTimeOffset StartedAt { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public bool TimedOut { get; set; }
    public bool HasMoreWork { get; set; }
    public int OutgoingSent { get; set; }
    public int OutgoingFailed { get; set; }
    public int ScheduledReleased { get; set; }
    public int IncomingRecovered { get; set; }
    public int ExpiredDeleted { get; set; }
    public int OtherProcessed { get; set; }
    public int Remaining { get; set; }
    public int FailedSteps { get; set; }
    public string[] SkippedStores { get; set; } = [];
    public DurabilityPassStoreResponse[] Stores { get; set; } = [];

    public static DurabilityPassResponse For(DurabilityPassReport report)
    {
        return new DurabilityPassResponse
        {
            StartedAt = report.StartedAt,
            ElapsedMilliseconds = report.Elapsed.TotalMilliseconds,
            TimedOut = report.TimedOut,
            HasMoreWork = report.HasMoreWork,
            OutgoingSent = report.OutgoingSent,
            OutgoingFailed = report.OutgoingFailed,
            ScheduledReleased = report.ScheduledReleased,
            IncomingRecovered = report.IncomingRecovered,
            ExpiredDeleted = report.ExpiredDeleted,
            OtherProcessed = report.OtherProcessed,
            Remaining = report.Remaining,
            FailedSteps = report.FailedSteps,
            SkippedStores = report.SkippedStores.Select(x => x.ToString()).ToArray(),
            Stores = report.Stores.Select(store => new DurabilityPassStoreResponse
            {
                StoreUri = store.StoreUri.ToString(),
                StoreName = store.StoreName,
                Lock = store.Lock.ToString(),
                Error = errorFor(store.Exception),
                Steps = store.Steps.Select(step => new DurabilityPassStepResponse
                {
                    StepName = step.StepName,
                    Status = step.Status.ToString(),
                    Calls = step.Calls,
                    ElapsedMilliseconds = step.Elapsed.TotalMilliseconds,
                    Processed = step.Result.Processed,
                    Remaining = step.Result.Remaining,
                    Error = errorFor(step.Exception)
                }).ToArray()
            }).ToArray()
        };
    }

    // GH-5037. Only the exception type: its message can carry connection details or data, and it is in the
    // application's log either way
    private static string? errorFor(Exception? exception) => exception?.GetType().Name;
}

/// <summary>
///     GH-5016. What one durability pass did against one message store
/// </summary>
public class DurabilityPassStoreResponse
{
    public string StoreUri { get; set; } = string.Empty;
    public string StoreName { get; set; } = string.Empty;
    public string Lock { get; set; } = string.Empty;
    public string? Error { get; set; }
    public DurabilityPassStepResponse[] Steps { get; set; } = [];
}

/// <summary>
///     GH-5016. What one step did in a durability pass
/// </summary>
public class DurabilityPassStepResponse
{
    public string StepName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int Calls { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public int Processed { get; set; }
    public int Remaining { get; set; }
    public string? Error { get; set; }
}

/// <summary>
///     GH-5037. Configures the durability pass endpoint. Mapping it requires an authorization decision, either
///     <see cref="ProtectedEndpointOptions.RequireAuthorization" /> or <see cref="ProtectedEndpointOptions.AllowAnonymous" />
/// </summary>
public class DurabilityEndpointOptions : ProtectedEndpointOptions
{
    /// <summary>
    ///     The largest <c>batchSize</c> a caller may ask for. A larger value is lowered to this. Default is 1,000
    /// </summary>
    public int MaxBatchSize { get; set; } = 1000;

    /// <summary>
    ///     The longest <c>maxDuration</c> a caller may ask for. A longer one is lowered to this. Default is 5 minutes
    /// </summary>
    public TimeSpan MaxDuration { get; set; } = 5.Minutes();
}

public static class DurabilityEndpointExtensions
{
    /// <summary>
    ///     GH-5016. Add an endpoint that runs one bounded durability pass, for a scheduler such as Cloud Scheduler,
    ///     EventBridge Scheduler or a timer-triggered function to call under
    ///     <see cref="DurabilityMode.ServerlessWithOutbox" />. <c>POST {groupUrlPrefix}/pass</c> runs the pass and
    ///     answers with a <see cref="DurabilityPassResponse" />, 200 when every step succeeded and 500 when any step
    ///     failed, or 409 when the application runs in any other durability mode. The optional <c>maxDuration</c>
    ///     (seconds) and <c>batchSize</c> query string values bound the pass, up to
    ///     <see cref="DurabilityEndpointOptions.MaxDuration" /> and <see cref="DurabilityEndpointOptions.MaxBatchSize" />.
    /// </summary>
    /// <remarks>
    ///     This endpoint sends messages and deletes rows, so <paramref name="configure" /> has to make an
    ///     authorization decision: <c>opts.RequireAuthorization("scheduler")</c>, or <c>opts.AllowAnonymous()</c>
    ///     when Cloud Run IAM/OIDC, a function key or the like already protects the application.
    /// </remarks>
    /// <param name="configure">Make the authorization decision and optionally tighten the caps</param>
    /// <param name="groupUrlPrefix">
    ///     Optionally override the group Url prefix for these endpoints. The default is "/_wolverine/durability"
    /// </param>
    public static RouteGroupBuilder MapWolverineDurabilityEndpoints(this IEndpointRouteBuilder endpoints,
        Action<DurabilityEndpointOptions> configure, string groupUrlPrefix = "/_wolverine/durability")
    {
        ArgumentNullException.ThrowIfNull(configure);

        if (groupUrlPrefix.IsEmpty())
        {
            throw new ArgumentNullException(nameof(groupUrlPrefix), "Cannot be empty or null");
        }

        var settings = new DurabilityEndpointOptions();
        configure(settings);
        settings.AssertDecided("the Wolverine durability endpoints");

        var group = endpoints.MapGroup(groupUrlPrefix);

        group.MapPost("/pass", async (IWolverineRuntime runtime, int? maxDuration, int? batchSize,
            CancellationToken cancellation) =>
        {
            if (!DurabilityPassRunner.IsAllowedIn(runtime.Options.Durability.Mode))
            {
                return Results.Problem(
                    $"A durability pass only runs under DurabilityMode.{nameof(DurabilityMode.ServerlessWithOutbox)}",
                    statusCode: StatusCodes.Status409Conflict);
            }

            var options = new DurabilityPassOptions
            {
                BatchSize = batchSize is > 0 ? Math.Min(batchSize.Value, settings.MaxBatchSize) : null
            };

            if (maxDuration is > 0)
            {
                var requested = maxDuration.Value.Seconds();
                options.MaxDuration = requested > settings.MaxDuration ? settings.MaxDuration : requested;
            }

            var report = await runtime.RunDurabilityPassAsync(options, cancellation);

            return Results.Json(DurabilityPassResponse.For(report),
                statusCode: report.FailedSteps > 0 ? StatusCodes.Status500InternalServerError : StatusCodes.Status200OK);
        });

        settings.ApplyTo(group);

        return group;
    }
}
