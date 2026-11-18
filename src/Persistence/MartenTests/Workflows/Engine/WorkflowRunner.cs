using Wolverine;
using System.Runtime.ExceptionServices;
using JasperFx.Events;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine.Marten;
using Wolverine.Runtime;

namespace MartenTests.Workflows.Engine;

/// <summary>
/// GH-5654 SPIKE. What the generated handler for every workflow message type calls: load the workflow's stream,
/// add the arriving message to it, replay the method, then append what the replay did and put its sends in the
/// outbox. The handler's Marten transaction commits the events, the snapshot and the outbox together.
/// </summary>
public static class WorkflowRunner
{
    /// <summary>For the spike report: how many events the last resume read from the stream</summary>
    public static int LastResumeEventsRead { get; private set; }

    public static async Task ResumeAsync(object message, Envelope envelope, IDocumentSession session,
        IMessageContext context, IWolverineRuntime runtime, WorkflowRegistry registry, CancellationToken cancellation)
    {
        var logger = runtime.LoggerFactory.CreateLogger(typeof(WorkflowRunner));

        var id = WorkflowIdentity.Find(message, envelope) ?? throw new InvalidOperationException(
            $"Could not determine the workflow id for a {message.GetType().Name}: it needs a [SagaIdentity] or Id member, or the saga-id header");

        // The inline snapshot plus the stream version for the optimistic concurrency check
        var stream = await session.Events.FetchForWriting<WorkflowState>(id, cancellation);

        WorkflowDefinition definition;
        List<IEvent> journal;
        var appending = new List<IEvent>();

        if (stream.Aggregate == null)
        {
            if (!registry.TryFindStarter(message.GetType(), out definition))
            {
                logger.LogWarning("No workflow {WorkflowId} exists for a {MessageType}; discarding it", id,
                    message.GetType().Name);
                return;
            }

            appending.Add(session.Events.BuildEvent(new WorkflowStarted(definition.Name, definition.Version,
                definition.Fingerprint)));
            appending.Add(session.Events.BuildEvent(message).WithHeader(WorkflowHeaders.Role, WorkflowHeaders.Trigger));
            journal = [..appending];
            LastResumeEventsRead = 0;
        }
        else
        {
            var state = stream.Aggregate;
            if (state.IsFinished)
            {
                logger.LogInformation("Workflow {WorkflowId} is {Status}; discarding a late {MessageType}", id,
                    state.Status, message.GetType().Name);
                return;
            }

            IEvent incoming;
            switch (message)
            {
                case WorkflowTimeout timeout when state.WaitingStep != timeout.Step:
                case WorkflowDelay delay when state.WaitingStep != delay.Step:
                    logger.LogDebug("Discarding a stale timer for workflow {WorkflowId}", id);
                    return;

                case WorkflowTimeout timeout:
                    incoming = session.Events.BuildEvent(new WorkflowTimedOut(timeout.Step));
                    break;

                case WorkflowDelay delay:
                    incoming = session.Events.BuildEvent(new WorkflowDelayElapsed(delay.Step));
                    break;

                default:
                    // Stored whether or not the workflow is waiting for it yet; a replay consumes it when it gets there
                    incoming = session.Events.BuildEvent(message)
                        .WithHeader(WorkflowHeaders.Role, WorkflowHeaders.Received);
                    break;
            }

            appending.Add(incoming);

            if (!registry.TryFind(state.WorkflowName, state.WorkflowVersion, out definition))
            {
                await faultAsync(stream, appending, session,
                    $"Workflow {state.WorkflowName} version {state.WorkflowVersion} no longer exists in this application",
                    cancellation);
                return;
            }

            var stored = await session.Events.FetchStreamAsync(id, stream.StartingVersion ?? 0,
                token: cancellation);
            LastResumeEventsRead = stored.Count;
            journal = [..stored, incoming];
        }

        var replay = new WorkflowReplay(id, definition, journal, data => session.Events.BuildEvent(data));

        await using (var scope = runtime.Services.CreateAsyncScope())
        {
            await replay.RunAsync(scope.ServiceProvider);
        }

        switch (replay.Outcome)
        {
            case ReplayOutcome.Faulted:
                await faultAsync(stream, appending, session, replay.FaultReason!, cancellation);
                return;

            case ReplayOutcome.Failed:
                // Nothing is appended and nothing is sent: the replay is retried by the error policy, and on the
                // final failure WorkflowCompensation sends the registered undo messages. This run's registrations
                // ride along on the exception, because the ones made after the last commit aren't in the journal.
                replay.Failure!.Data[WorkflowCompensation.RegistrationsKey] = replay.Registrations.ToArray();
                ExceptionDispatchInfo.Capture(replay.Failure!).Throw();
                return;
        }

        appending.AddRange(replay.NewEvents);
        if (replay.Outcome == ReplayOutcome.Completed)
        {
            appending.Add(session.Events.BuildEvent(new WorkflowCompleted()));
        }

        stream.AppendMany(appending);

        foreach (var outgoing in replay.Outgoing)
        {
            if (outgoing.Publish)
            {
                await context.PublishAsync(outgoing.Message, outgoing.Options);
            }
            else
            {
                await context.SendAsync(outgoing.Message, outgoing.Options);
            }
        }

        // The transactional middleware around the handler calls SaveChangesAsync() and flushes the outbox
    }

    private static async Task faultAsync(IEventStream<WorkflowState> stream, List<IEvent> appending,
        IDocumentSession session, string reason, CancellationToken cancellation)
    {
        appending.Add(session.Events.BuildEvent(new WorkflowFaulted(reason)));
        stream.AppendMany(appending);

        // Commit the Faulted state, then throw so the resume message is dead-lettered. Retrying that dead letter
        // after a fix is deployed is how a faulted instance is retried (not implemented in the spike).
        await session.SaveChangesAsync(cancellation);
        throw new WorkflowReplayMismatchException(reason);
    }
}

/// <summary>
/// GH-5654 SPIKE. Runs after the error policy gives up on a workflow message: sends every registered undo message
/// in LIFO order in one outbox transaction, against a freshly loaded stream.
/// </summary>
public static class WorkflowCompensation
{
    public const string RegistrationsKey = "wolverine-workflow-registrations";

    public static async ValueTask CompensateAsync(IWolverineRuntime runtime, IEnvelopeLifecycle lifecycle,
        Exception exception)
    {
        var envelope = lifecycle.Envelope;
        if (envelope?.Message == null) return;

        var id = WorkflowIdentity.Find(envelope.Message, envelope);
        if (id == null) return;

        var store = runtime.Services.GetRequiredService<IDocumentStore>();
        await using var session = store.LightweightSession();
        var outbox = new MartenOutbox(runtime, session);

        var stream = await session.Events.FetchForWriting<WorkflowState>(id.Value);
        if (stream.Aggregate == null || stream.Aggregate.IsFinished) return;

        var events = await session.Events.FetchStreamAsync(id.Value, stream.StartingVersion ?? 0);

        // The failed run's own registrations if we have them; otherwise (say the process died, and this is a
        // recovered message failing for good) the registrations committed to the journal
        var registrations = exception.Data[RegistrationsKey] as object[] ?? events
            .Select(x => x.Data)
            .OfType<WorkflowFailureRegistered>()
            .Select(x => System.Text.Json.JsonSerializer.Deserialize(x.Json, Type.GetType(x.MessageType, true)!)!)
            .ToArray();

        var undoMessages = registrations.Reverse().ToArray();

        stream.AppendOne(new SagaCompensating(envelope.Message.GetType().Name, exception.GetType().FullName!,
            exception.Message));

        foreach (var undo in undoMessages)
        {
            // Recorded in the journal too, so the timeline shows what compensation sent
            stream.AppendOne(session.Events.BuildEvent(undo).WithHeader(WorkflowHeaders.Role, WorkflowHeaders.Undo));
            await outbox.SendAsync(undo, new DeliveryOptions { SagaId = id.Value.ToString() });
        }

        stream.AppendOne(new SagaCompensated(undoMessages.Length));

        await session.SaveChangesAsync();
    }
}
