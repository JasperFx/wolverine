using Microsoft.Extensions.Logging;
using Wolverine.Transports.Local;

namespace Wolverine.Configuration;

internal enum ListenerConfigurationSeverity
{
    /// <summary>
    /// The setting is inert, but the endpoint still does something reasonable. Log and carry on.
    /// </summary>
    Warning,

    /// <summary>
    /// The setting asked for a processing guarantee the endpoint's mode cannot deliver. Refuse to start.
    /// </summary>
    Fatal
}

internal record ListenerConfigurationProblem(
    Endpoint Endpoint,
    ListenerConfigurationSeverity Severity,
    string Message);

/// <summary>
/// GH-3712. Several listener configuration combinations used to be accepted in silence and then do
/// nothing at runtime -- most damagingly <c>ProcessInline()</c> together with
/// <c>PartitionProcessingByGroupId()</c>, which reads as "messages sharing a group id never run
/// concurrently" and delivered no such thing. Everything Wolverine's Inline listening path ignores is
/// enumerated here so a misconfiguration is either refused at bootstrap or said out loud in the log.
/// </summary>
internal static class ListenerConfigurationValidator
{
    /// <summary>
    /// Validate every listening endpoint, logging the warnings and throwing on the first fatal problem.
    /// Expects the endpoints to have already been compiled, so that endpoint policies and delayed
    /// configuration have had their say about the final mode.
    /// </summary>
    /// <param name="requeuePoliciesConfigured">
    /// GH-4060. True when the application configured any error handling that hands a failed message back to its
    /// listener for redelivery. Failure policies live on the handler graph rather than on an endpoint, so the
    /// endpoint-scoped checks below cannot work this out for themselves and it is computed once by the caller.
    /// </param>
    internal static void AssertValid(IEnumerable<Endpoint> endpoints, ILogger? logger,
        bool requeuePoliciesConfigured = false)
    {
        var problems = endpoints.SelectMany(x => Validate(x, requeuePoliciesConfigured)).ToArray();

        foreach (var problem in problems.Where(x => x.Severity == ListenerConfigurationSeverity.Warning))
        {
            logger?.LogWarning(problem.Message);
        }

        var fatal = problems.FirstOrDefault(x => x.Severity == ListenerConfigurationSeverity.Fatal);
        if (fatal != null)
        {
            throw new InvalidListenerConfigurationException(fatal.Message);
        }
    }

    internal static IEnumerable<ListenerConfigurationProblem> Validate(Endpoint endpoint,
        bool requeuePoliciesConfigured = false)
    {
        if (!endpoint.IsListener && endpoint is not LocalQueue)
        {
            yield break;
        }

        // GH-4047. Transport-specific constraints first, and deliberately outside the Inline-only gate below: they
        // are about combinations no core rule can see, and the Pulsar one they were added for is about NativeAck.
        foreach (var message in endpoint.validateModeConfiguration())
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Fatal, message);
        }

        // GH-4060. Deliberately ahead of the Inline-only gate below: a listener with no cursor to redeliver from
        // ignores requeue policies in EVERY mode, not just Inline.
        if (requeuePoliciesConfigured && !endpoint.supportsRedelivery)
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Warning,
                $"Ignored listener configuration for {describe(endpoint)}: this listener reads through an ephemeral, " +
                "non-durable cursor -- Pulsar's TailFromLatest() is the one transport feature that does this today -- so it " +
                "has nothing to redeliver a message from. Requeue(), RequeueIndefinitely(), PauseThenRequeue() and " +
                "MaximumAttempts() all resolve to DeferAsync(), which is necessarily a no-op here: a message that fails its " +
                "handler is dropped rather than retried, and no error is raised. Error handling that never leaves the " +
                "process still works -- RetryTimes(), RetryWithCooldown() and ScheduleRetry() all run normally -- and a " +
                "configured message store still captures the failure in the durable dead letter table. Use those instead, " +
                "or drop TailFromLatest() for an ordinary subscription if these messages are ones you cannot afford to lose.");
        }

        // GH-3710. The in-memory guard applies to every non-durable listening mode, so this check sits
        // ahead of the Inline-only block below.
        if (endpoint.InMemoryIdempotency != null)
        {
            if (endpoint.Mode == EndpointMode.Durable)
            {
                yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Warning,
                    $"Ignored listener configuration for {describe(endpoint)}: WithInMemoryIdempotency() was configured on a " +
                    "durable endpoint, and Wolverine has not built the guard. The durable inbox already rejects a duplicate " +
                    "message id on the primary key of the incoming table, across restarts and across every node -- which is " +
                    "strictly stronger than an in-memory, per-process guard. Remove WithInMemoryIdempotency(), or remove " +
                    "UseDurableInbox() if what you wanted was best-effort dedup without a database.");
            }
            else if (endpoint is LocalQueue)
            {
                yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Warning,
                    $"Ignored listener configuration for {describe(endpoint)}: WithInMemoryIdempotency() was configured on a " +
                    "local queue. The guard deduplicates redeliveries from a message broker, and nothing is ever redelivered " +
                    "to a local queue -- its messages are enqueued from inside this same process.");
            }
        }

        if (endpoint.Mode != EndpointMode.Inline)
        {
            yield break;
        }

        // NativeAck reaches none of the checks below and must not: partitioned processing plus real parallelism
        // is the entire point of that mode. It is Inline -- and only Inline -- that has no execution block.
        var name = describe(endpoint);

        if (endpoint is LocalQueue)
        {
            // GH-4022. ListenerConfiguration.ProcessInline() refuses this eagerly, but only when it was handed the
            // queue itself. LocalQueueFor<T>()/IConfigureLocalQueue resolve their queue lazily through
            // LocalTransport.ConfigureQueueFor(), so the eager guard cannot see a LocalQueue there and the
            // mode is not settled until Compile(). Catch that path here instead of at LocalQueue.BuildAgent(),
            // which does not run until something first sends to the queue.
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Fatal,
                $"Invalid listener configuration for {name}: ProcessInline() was configured on a local queue. Inline means " +
                "\"execute the message on the transport's own listening callback instead of queueing it\", and a local queue has no " +
                "transport listener -- the queue itself is Wolverine's local execution block, so there would be nothing left to run " +
                "the message. Use BufferedInMemory() (the default for a local queue) or UseDurableInbox(), plus Sequential() if what " +
                "you wanted was one message at a time.");

            // Everything below is about settings an Inline endpoint merely ignores. This queue is not
            // going to start at all, so there is no point piling warnings on top of the reason why.
            yield break;
        }

        if (endpoint.GroupShardingSlotNumber.HasValue)
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Fatal,
                $"Invalid listener configuration for {name}: PartitionProcessingByGroupId() was configured on an Inline endpoint. " +
                "Partitioned processing is implemented by Wolverine's local execution block, and an Inline endpoint executes each message " +
                "directly on the transport's listening callback without one -- so the group id ordering guarantee would silently not exist. " +
                "If you want partitioned processing AND native broker acks, use ProcessInParallelWithNativeAcks() " +
                "instead of ProcessInline() -- that mode exists for exactly this combination (GH-3708). Otherwise use " +
                "BufferedInMemory() or UseDurableInbox(), or remove PartitionProcessingByGroupId() from this endpoint.");
        }

        if (endpoint.DiscardedMaxDegreeOfParallelism is { } discarded)
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Warning,
                $"Ignored listener configuration for {name}: a maximum parallelism of {discarded} was configured on an Inline endpoint, " +
                "and Wolverine has reset it to 1. MaximumParallelMessages(), Sequential(), ExclusiveNodeWithParallelism() and " +
                "ExclusiveNodeWithSessionOrdering() all size Wolverine's local execution block, which an Inline endpoint does not have. " +
                "How many messages an Inline listener handles at once is decided by the transport's own listener (for example RabbitMQ's " +
                "ConsumerDispatchConcurrency). Use BufferedInMemory() or UseDurableInbox() if you want Wolverine to govern the parallelism.");
        }

        if (endpoint.BufferingLimitsAreExplicit)
        {
            yield return new ListenerConfigurationProblem(endpoint, ListenerConfigurationSeverity.Warning,
                $"Ignored listener configuration for {name}: BufferingLimits were configured on an Inline endpoint. Back pressure is applied " +
                "by counting messages queued in Wolverine's local execution block, so an Inline endpoint never starts a BackPressureAgent " +
                "and these limits do nothing. Use BufferedInMemory(limits) or UseDurableInbox(limits) if you want back pressure.");
        }
    }

    private static string describe(Endpoint endpoint)
    {
        return endpoint.EndpointName == endpoint.Uri.ToString()
            ? $"endpoint '{endpoint.Uri}'"
            : $"endpoint '{endpoint.EndpointName}' ({endpoint.Uri})";
    }
}

/// <summary>
/// GH-3712. Thrown at bootstrap when a listening endpoint's configuration asks for a processing
/// guarantee that its <see cref="EndpointMode"/> cannot deliver.
/// </summary>
public class InvalidListenerConfigurationException : Exception
{
    public InvalidListenerConfigurationException(string message) : base(message)
    {
    }
}
