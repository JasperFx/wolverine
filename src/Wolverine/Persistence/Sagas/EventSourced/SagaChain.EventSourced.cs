using System.Diagnostics.CodeAnalysis;
using JasperFx;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

// GH-5415: the code an event-sourced saga's chain generates
public partial class SagaChain
{
    /// <summary>GH-5415. Is this chain's saga an <see cref="EventSourcedSaga" />?</summary>
    public bool IsEventSourced => SagaType.CanBeCastTo<EventSourcedSaga>();

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "SagaType originates from a HandlerCall's HandlerType, which is application-rooted via HandlerDiscovery. Codegen-time only.")]
    private List<Frame> determineEventSourcedFrames(JasperFx.CodeGeneration.GenerationRules rules,
        IServiceContainer container, IPersistenceFrameProvider frameProvider)
    {
        if (frameProvider is not IEventSourcingFrameProvider eventSourcing)
        {
            throw new InvalidSagaException(
                $"{SagaType.FullNameInCode()} is an event-sourced saga, but its storage ({frameProvider.GetType().FullNameInCode()}) is not an event store. " +
                "Event-sourced sagas are kept by Marten, Polecat or Fisher: integrate one with IntegrateWithWolverine().");
        }

        if (IdNotProvidedCalls.Length != 0)
        {
            throw new InvalidSagaException(
                $"{SagaType.FullNameInCode()} declares {IdNotProvided}(), which event-sourced sagas do not support yet.");
        }

        var sagaCreator = StartingCalls.Concat(ExistingCalls).Concat(NotFoundCalls).Concat(CompensateCalls)
            .FirstOrDefault(x => x.Creates.Any(v => v.VariableType == SagaType));
        if (sagaCreator != null)
        {
            throw new InvalidSagaException(
                $"{SagaType.FullNameInCode()}.{sagaCreator.Method.Name}() returns the saga itself. An event-sourced saga's methods return events and messages, never the saga: its state comes only from its events.");
        }

        IsTransactional = true;
        ReturnVariableActionSource = new EventSourcedSagaReturnActionSource(SagaType, eventSourcing.StoreName);

        var findSagaId = SagaIdMember == null
            ? (Frame)new PullSagaIdFromEnvelopeFrame(SagaType, frameProvider.DetermineSagaIdType(SagaType, this, container))
            : new PullSagaIdFromMessageFrame(MessageType, SagaType, SagaIdMember);
        var sagaId = findSagaId.Creates.First();

        var load = eventSourcing.BuildLoadAggregateFrame(new AggregateLoadRequest(SagaType, sagaId,
            ModelConcurrencyStyle.Optimistic, null, false, false));
        var stream = AggregateHandling.FindEventStreamVariable(load, SagaType, eventSourcing);

        var guard = new EventSourcedSagaGuardFrame(SagaType, stream);
        var saga = guard.Saga;
        var archiveWhenCompleted = SagaType.HasAttribute<ArchiveWhenCompletedAttribute>();

        // Messages a NotFound method returns are sent: there is no stream to append them to
        foreach (var call in NotFoundCalls)
        {
            foreach (var created in call.Creates) created.UseReturnAction(new CascadeMessage(created));
        }

        var nullPath = eventSourcedNullPath(container, frameProvider, sagaId, saga, stream, archiveWhenCompleted).ToArray();
        var completedPath = eventSourcedCompletedPath(sagaId, saga).ToArray();
        var existsPath = eventSourcedExistsPath(container, frameProvider, sagaId, saga, stream, archiveWhenCompleted).ToArray();

        guard.UsePaths(nullPath, completedPath, existsPath);

        var list = new List<Frame> { findSagaId, load, guard };

        return Middleware.Concat(container.TryCreateConstructorFrames(Handlers)).Concat(list).Concat(Postprocessors)
            .Concat(PostCommitPostprocessors)
            .ToList();
    }

    private IEnumerable<Frame> eventSourcedNullPath(IServiceContainer container, IPersistenceFrameProvider frameProvider,
        Variable sagaId, Variable saga, Variable stream, bool archiveWhenCompleted)
    {
        if (MessageType.CanBeCastTo<TimeoutMessage>())
        {
            yield return new ReturnFrame();
            yield break;
        }

        if (StartingCalls.Length != 0)
        {
            // StartOrHandle, or an instance Start, works on a new saga with nothing applied to it yet
            if (StartingCalls.Any(x => !x.Method.IsStatic))
            {
                yield return new CreateMissingSagaFrame(saga);
            }

            yield return new SetSagaIdFrame(sagaId, SagaType);

            foreach (var call in StartingCalls)
            {
                yield return call;
                foreach (var frame in call.Creates.SelectMany(x => x.ReturnAction(this).Frames())) yield return frame;
            }

            yield return new AfterEventSourcedSagaHandlingFrame(stream, saga, archiveWhenCompleted);
            yield return new TranslateStreamCollisionFrame(frameProvider.CommitUnitOfWorkFrame(saga, container), SagaType,
                sagaId);
        }
        else if (NotFoundCalls.Length != 0)
        {
            foreach (var call in NotFoundCalls)
            {
                call.TrySetArgument(sagaId);
                yield return call;
                foreach (var frame in call.Creates.SelectMany(x => x.ReturnAction(this).Frames())) yield return frame;
            }
        }
        else
        {
            yield return new AssertSagaStateExistsFrame(saga, sagaId);
        }
    }

    // As DetermineRetainedSagaCompletedSteps, except that the messages the copied NotFound methods return are sent: the
    // saga's own return value source would append them to its stream
    private IEnumerable<Frame> eventSourcedCompletedPath(Variable sagaId, Variable saga)
    {
        if (MessageType.CanBeCastTo<TimeoutMessage>())
        {
            yield return new ReturnFrame();
            yield break;
        }

        if (NotFoundCalls.Length != 0)
        {
            foreach (var original in NotFoundCalls)
            {
                var call = new MethodCall(original.HandlerType, original.Method) { CommentText = original.CommentText };
                foreach (var alias in original.Aliases) call.Aliases[alias.Key] = alias.Value;

                call.TrySetArgument(sagaId);
                yield return call;
                foreach (var created in call.Creates)
                {
                    foreach (var frame in new CascadeMessage(created).Frames()) yield return frame;
                }
            }
        }
        else if (StartingCalls.Length != 0)
        {
            yield return new SkipStartOfCompletedSagaFrame(SagaType, sagaId);
            yield return new ReturnFrame();
        }
        else
        {
            yield return new AssertSagaStateExistsFrame(saga, sagaId);
        }
    }

    private IEnumerable<Frame> eventSourcedExistsPath(IServiceContainer container, IPersistenceFrameProvider frameProvider,
        Variable sagaId, Variable saga, Variable stream, bool archiveWhenCompleted)
    {
        // A message that only starts the saga, for a saga that already exists, is refused as a duplicate start
        if (ExistingCalls.Length == 0 && CompensateCalls.Length == 0 && StartingCalls.Length != 0)
        {
            yield return new RefuseStartOfExistingSagaFrame(SagaType, sagaId);
            yield break;
        }

        yield return new SetSagaIdFrame(sagaId, SagaType);

        foreach (var call in ExistingCalls)
        {
            yield return call;
            foreach (var frame in call.Creates.SelectMany(x => x.ReturnAction(this).Frames())) yield return frame;
        }

        if (CompensateCalls.Length != 0)
        {
            yield return new CompensateEventSourcedSagaFrame(stream, MessageType);
        }

        yield return new AfterEventSourcedSagaHandlingFrame(stream, saga, archiveWhenCompleted);
        yield return frameProvider.CommitUnitOfWorkFrame(saga, container);
    }
}
