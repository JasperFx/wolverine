using Wolverine;
using System.Reflection;
using JasperFx;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using JasperFx.Events.Projections;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Attributes;
using Wolverine.ErrorHandling;
using Wolverine.Runtime;
using Wolverine.Runtime.Handlers;

namespace MartenTests.Workflows.Engine;

/// <summary>
/// GH-5654 SPIKE. One ordinary Wolverine handler per message type a workflow receives (closed per type at startup),
/// so the codegen, the Marten transaction, the outbox and the error policies are all the normal ones.
/// </summary>
public static class WorkflowHandler<T> where T : class
{
    public static void Configure(HandlerChain chain)
    {
        // Two messages for one workflow at once: the loser of the stream version check replays again
        chain.OnException<ConcurrencyException>()
            .RetryWithCooldown(50.Milliseconds(), 100.Milliseconds(), 250.Milliseconds(), 500.Milliseconds());

        // A replay that no longer matches its journal can't be helped by a retry
        chain.OnException<WorkflowReplayMismatchException>().MoveToErrorQueue();

        // The workflow method threw. A replay is idempotent, so retrying is safe; once the retries are used up the
        // registered undo messages go out and the message is dead-lettered.
        chain.OnAnyException().RetryTimes(2)
            .Then.MoveToErrorQueue().And(WorkflowCompensation.CompensateAsync, "Compensate the durable workflow");
    }

    [Transactional]
    public static Task Handle(T message, Envelope envelope, IDocumentSession session, IMessageContext context,
        IWolverineRuntime runtime, WorkflowRegistry registry, CancellationToken cancellation)
    {
        return WorkflowRunner.ResumeAsync(message, envelope, session, context, runtime, registry, cancellation);
    }
}

public static class DurableWorkflowExtensions
{
    /// <summary>
    /// GH-5654 SPIKE. Register every [DurableWorkflow] method on these types: one handler per received message
    /// type, Marten event headers, and the inline WorkflowState snapshot.
    /// </summary>
    public static WolverineOptions AddDurableWorkflows(this WolverineOptions opts, params Type[] workflowTypes)
    {
        var definitions = workflowTypes
            .SelectMany(x => x.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
            .Where(x => x.HasAttribute<DurableWorkflowAttribute>())
            .Select(x => new WorkflowDefinition(x))
            .ToArray();

        var registry = new WorkflowRegistry(definitions);
        opts.Services.AddSingleton(registry);

        foreach (var messageType in registry.HandledMessageTypes())
        {
            opts.Discovery.IncludeType(typeof(WorkflowHandler<>).MakeGenericType(messageType));
        }

        opts.Services.ConfigureMarten(m =>
        {
            // The "wf" header tells a sent message from a received one of the same type
            m.Events.MetadataConfig.HeadersEnabled = true;
            m.Projections.Add(new WorkflowStateProjection(), ProjectionLifecycle.Inline);
        });

        return opts;
    }
}
