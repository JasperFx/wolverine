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
                Error = store.Exception?.Message,
                Steps = store.Steps.Select(step => new DurabilityPassStepResponse
                {
                    StepName = step.StepName,
                    Status = step.Status.ToString(),
                    Calls = step.Calls,
                    ElapsedMilliseconds = step.Elapsed.TotalMilliseconds,
                    Processed = step.Result.Processed,
                    Remaining = step.Result.Remaining,
                    Error = step.Exception?.Message
                }).ToArray()
            }).ToArray()
        };
    }
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

public static class DurabilityEndpointExtensions
{
    /// <summary>
    ///     GH-5016. Add an endpoint that runs one bounded durability pass, for a scheduler such as Cloud Scheduler,
    ///     EventBridge Scheduler or a timer-triggered function to call under
    ///     <see cref="DurabilityMode.ServerlessWithOutbox" />. <c>POST {groupUrlPrefix}/pass</c> runs the pass and
    ///     answers with a <see cref="DurabilityPassResponse" />, 200 when every step succeeded and 500 when any step
    ///     failed. The optional <c>maxDuration</c> (seconds) and <c>batchSize</c> query string values bound the pass.
    /// </summary>
    /// <remarks>
    ///     This endpoint sends messages and deletes rows, so <b>protect it</b> before you deploy it: Cloud Run
    ///     IAM/OIDC, function keys, or an authorization policy through the returned <see cref="RouteGroupBuilder" />,
    ///     for example <c>app.MapWolverineDurabilityEndpoints().RequireAuthorization("scheduler")</c>.
    /// </remarks>
    /// <param name="groupUrlPrefix">
    ///     Optionally override the group Url prefix for these endpoints. The default is "/_wolverine/durability"
    /// </param>
    public static RouteGroupBuilder MapWolverineDurabilityEndpoints(this IEndpointRouteBuilder endpoints,
        string groupUrlPrefix = "/_wolverine/durability")
    {
        if (groupUrlPrefix.IsEmpty())
        {
            throw new ArgumentNullException(nameof(groupUrlPrefix), "Cannot be empty or null");
        }

        var group = endpoints.MapGroup(groupUrlPrefix);

        group.MapPost("/pass", async (IWolverineRuntime runtime, int? maxDuration, int? batchSize,
            CancellationToken cancellation) =>
        {
            var options = new DurabilityPassOptions { BatchSize = batchSize };
            if (maxDuration is > 0)
            {
                options.MaxDuration = maxDuration.Value.Seconds();
            }

            var report = await runtime.RunDurabilityPassAsync(options, cancellation);

            return Results.Json(DurabilityPassResponse.For(report),
                statusCode: report.FailedSteps > 0 ? StatusCodes.Status500InternalServerError : StatusCodes.Status200OK);
        });

        return group;
    }
}
