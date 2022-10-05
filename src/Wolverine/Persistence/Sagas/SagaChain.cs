using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Baseline;
using Baseline.Reflection;
using Lamar;
using LamarCodeGeneration;
using LamarCodeGeneration.Frames;
using LamarCodeGeneration.Model;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

internal class SagaChain : HandlerChain
{
    private readonly Type _sagaType;
    public const string Orchestrate = "Orchestrate";
    public const string Orchestrates = "Orchestrates";
    public const string Start = "Start";
    public const string Starts = "Starts";
    public const string StartOrHandle = "StartOrHandle";
    public const string StartsOrHandles = "StartsOrHandles";
    public const string NotFound = "NotFound";

    public SagaChain(IGrouping<Type, HandlerCall> grouping, HandlerGraph parent) : base(grouping, parent)
    {
        try
        {
            _sagaType = grouping.Where(x => x.HandlerType.CanBeCastTo<Saga>()).Select(x => x.HandlerType)
                .Distinct().Single();
        }
        catch (Exception e)
        {
            var handlerTypes = grouping.Where(x => x.HandlerType.CanBeCastTo<Saga>())
                .Select(x => x.HandlerType).Select(x => x.FullNameInCode()).Join(", ");

            throw new InvalidSagaException(
                $"Command types cannot be handled by multiple saga types. Message {MessageType.FullNameInCode()} is handled by sagas {handlerTypes}", e);
        }

        SagaIdMember = DetermineSagaIdMember(MessageType);
    }

    internal static MemberInfo DetermineSagaIdMember(Type messageType)
    {
        var members = messageType.GetFields().OfType<MemberInfo>().Concat(messageType.GetProperties());
        return members.FirstOrDefault(x => x.HasAttribute<SagaIdentityAttribute>())
                       ?? members.FirstOrDefault(x => x.Name == SagaIdMemberName) ??
                       members.FirstOrDefault(x => x.Name.EqualsIgnoreCase("Id"));
    }

    public MemberInfo? SagaIdMember { get; set; }

    public MethodCall[] ExistingCalls { get; set; }

    public MethodCall[] StartingCalls { get; set; }

    public MethodCall[] NotFoundCalls { get; set; }

    private MethodCall[] findByNames(params string[] methodNames)
    {
        return Handlers.Where(x => methodNames.Contains(x.Method.Name) && x.HandlerType.CanBeCastTo<Saga>()).ToArray();
    }

    internal override List<Frame> DetermineFrames(GenerationRules rules, IContainer container)
    {
        applyCustomizations(rules, container);

        var frameProvider = rules.GetSagaPersistence();

        NotFoundCalls = findByNames(NotFound);
        StartingCalls = findByNames(Start, Starts, StartOrHandle, StartsOrHandles);

        ExistingCalls = findByNames(Orchestrate, Orchestrates, StartOrHandle, StartsOrHandles, Handle, Handles,
            Consume, Consumes);

        Handlers.Clear();

        if (!ExistingCalls.Any())
        {
            generateForOnlyStartingSaga(container, frameProvider);
        }
        else
        {
            generateCodeForMaybeExisting(container, frameProvider);
        }

        return Middleware.Concat(Postprocessors).ToList();
    }

    private void generateCodeForMaybeExisting(IContainer container, ISagaPersistenceFrameProvider frameProvider)
    {
        var findSagaId = SagaIdMember == null ? (Frame)new PullSagaIdFromEnvelopeFrame(frameProvider.DetermineSagaIdType(_sagaType, container)) : new PullSagaIdFromMessageFrame(MessageType, SagaIdMember);
        Postprocessors.Insert(0, findSagaId);

        var sagaId = findSagaId.Creates.Single();

        var load = frameProvider.DetermineLoadFrame(container, _sagaType, sagaId);
        var saga = load.Creates.Single();
        Postprocessors.Add(load);



        var startingFrames = DetermineSagaDoesNotExistSteps(sagaId, saga, frameProvider, container).ToArray();
        var existingFrames = DetermineSagaExistsSteps(sagaId, saga, frameProvider, container).ToArray();
        var ifNullBlock = new IfNullGuard(saga, startingFrames,
            existingFrames);

        Postprocessors.Add(ifNullBlock);
    }

    private void generateForOnlyStartingSaga(IContainer container, ISagaPersistenceFrameProvider frameProvider)
    {
        var creator = new CreateNewSagaFrame(_sagaType);
        Postprocessors.Add(creator);

        foreach (var call in StartingCalls)
        {
            Postprocessors.Add(call);
            foreach (var create in call.Creates)
            {
                Postprocessors.Add(new CaptureCascadingMessages(create));
            }
        }

        var ifNotCompleted = buildFrameForConditionalInsert(creator.Saga, frameProvider, container);
        Postprocessors.Add(ifNotCompleted);
    }

    internal IEnumerable<Frame> DetermineSagaDoesNotExistSteps(Variable sagaId, Variable saga, ISagaPersistenceFrameProvider frameProvider, IContainer container)
    {
        if (MessageType.CanBeCastTo<TimeoutMessage>())
        {
            yield return new ReturnFrame();
            yield break;
        }

        if (StartingCalls.Any())
        {
            yield return new CreateMissingSagaFrame(saga);

            foreach (var call in StartingCalls)
            {
                yield return call;
                foreach (var create in call.Creates)
                {
                    yield return new CaptureCascadingMessages(create);
                }
            }

            var ifNotCompleted = buildFrameForConditionalInsert(saga, frameProvider, container);
            yield return ifNotCompleted;
        }
        else if (NotFoundCalls.Any())
        {
            foreach (var call in NotFoundCalls)
            {
                call.TrySetArgument(sagaId);
                yield return call;
                foreach (var create in call.Creates)
                {
                    yield return new CaptureCascadingMessages(create);
                }
            }
        }
        else
        {
            yield return new AssertSagaStateExistsFrame(saga, sagaId);
        }
    }

    private static Frame buildFrameForConditionalInsert(Variable saga, ISagaPersistenceFrameProvider frameProvider,
        IContainer container)
    {
        var insert = frameProvider.DetermineInsertFrame(saga, container);
        var commit = frameProvider.CommitUnitOfWorkFrame(saga, container);
        return new ConditionalSagaInsertFrame(saga, insert, commit);
    }

    internal IEnumerable<Frame> DetermineSagaExistsSteps(Variable sagaId, Variable saga, ISagaPersistenceFrameProvider frameProvider, IContainer container)
    {
        foreach (var call in ExistingCalls)
        {
            yield return call;
            foreach (var create in call.Creates)
            {
                yield return new CaptureCascadingMessages(create);
            }
        }

        var update = frameProvider.DetermineUpdateFrame(saga, container);
        var delete = frameProvider.DetermineDeleteFrame(sagaId, saga, container);

        yield return new SagaStoreOrDeleteFrame(saga, update, delete);

        yield return frameProvider.CommitUnitOfWorkFrame(saga, container);
    }

    public const string SagaIdMemberName = "SagaId";
}
