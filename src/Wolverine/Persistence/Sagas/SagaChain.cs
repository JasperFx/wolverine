using System.Reflection;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Lamar;
using Wolverine.Runtime.Handlers;

namespace Wolverine.Persistence.Sagas;

public class SagaChain : HandlerChain
{
    public const string Orchestrate = "Orchestrate";
    public const string Orchestrates = "Orchestrates";
    public const string Start = "Start";
    public const string Starts = "Starts";
    public const string StartOrHandle = "StartOrHandle";
    public const string StartsOrHandles = "StartsOrHandles";
    public const string NotFound = "NotFound";

    public const string SagaIdMemberName = "SagaId";
    public const string SagaIdVariableName = "sagaId";
    public static readonly Type[] ValidSagaIdTypes = { typeof(Guid), typeof(int), typeof(long), typeof(string) };

    public SagaChain(IGrouping<Type, HandlerCall> grouping, HandlerGraph parent) : base(grouping, parent)
    {
        try
        {
            SagaType = grouping.Where(x => x.HandlerType.CanBeCastTo<Saga>()).Select(x => x.HandlerType)
                .Distinct().Single();
        }
        catch (Exception e)
        {
            var handlerTypes = grouping.Where(x => x.HandlerType.CanBeCastTo<Saga>())
                .Select(x => x.HandlerType).Select(x => x.FullNameInCode()).Join(", ");

            throw new InvalidSagaException(
                $"Command types cannot be handled by multiple saga types. Message {MessageType.FullNameInCode()} is handled by sagas {handlerTypes}",
                e);
        }

        SagaIdMember = DetermineSagaIdMember(MessageType, SagaType);
    }

    public Type SagaType { get; }

    public MemberInfo? SagaIdMember { get; set; }

    public MethodCall[] ExistingCalls { get; set; } = Array.Empty<MethodCall>();

    public MethodCall[] StartingCalls { get; set; } = Array.Empty<MethodCall>();

    public MethodCall[] NotFoundCalls { get; set; } = Array.Empty<MethodCall>();

    internal static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType)
    {
        var members = messageType.GetFields().OfType<MemberInfo>().Concat(messageType.GetProperties()).ToArray();
        return members.FirstOrDefault(x => x.HasAttribute<SagaIdentityAttribute>())
               ?? members.FirstOrDefault(x => x.Name.EqualsIgnoreCase($"{sagaType.Name}Id"))
               ?? members.FirstOrDefault(x => x.Name == SagaIdMemberName) ??
               members.FirstOrDefault(x => x.Name.EqualsIgnoreCase("Id"));
    }

    private MethodCall[] findByNames(params string[] methodNames)
    {
        return Handlers.Where(x => methodNames.Contains(x.Method.Name) && x.HandlerType.CanBeCastTo<Saga>()).ToArray();
    }

    internal override List<Frame> DetermineFrames(GenerationRules rules, IContainer container,
        MessageVariable messageVariable)
    {
        applyCustomizations(rules, container);

        var frameProvider = rules.GetPersistenceProviders(this, container);

        NotFoundCalls = findByNames(NotFound);
        StartingCalls = findByNames(Start, Starts, StartOrHandle, StartsOrHandles);

        ExistingCalls = findByNames(Orchestrate, Orchestrates, StartOrHandle, StartsOrHandles, Handle, Handles,
            Consume, Consumes);

        Handlers.Clear();

        var list = new List<Frame>();

        if (!ExistingCalls.Any())
        {
            generateForOnlyStartingSaga(container, frameProvider, list);
        }
        else
        {
            generateCodeForMaybeExisting(container, frameProvider, list);
        }

// .Concat(handlerReturnValueFrames)
        return Middleware.Concat(list).Concat(Postprocessors).ToList();
    }

    private void generateCodeForMaybeExisting(IContainer container, IPersistenceFrameProvider frameProvider,
        List<Frame> frames)
    {
        var findSagaId = SagaIdMember == null
            ? (Frame)new PullSagaIdFromEnvelopeFrame(frameProvider.DetermineSagaIdType(SagaType, container))
            : new PullSagaIdFromMessageFrame(MessageType, SagaIdMember);
        frames.Add(findSagaId);

        var sagaId = findSagaId.Creates.Single();

        var load = frameProvider.DetermineLoadFrame(container, SagaType, sagaId);
        var saga = load.Creates.Single();
        frames.Add(load);

        var startingFrames = DetermineSagaDoesNotExistSteps(sagaId, saga, frameProvider, container).ToArray();
        var existingFrames = DetermineSagaExistsSteps(sagaId, saga, frameProvider, container).ToArray();
        var ifNullBlock = new IfElseNullGuardFrame(saga, startingFrames,
            existingFrames);

        frames.Add(ifNullBlock);
    }

    private void generateForOnlyStartingSaga(IContainer container, IPersistenceFrameProvider frameProvider,
        List<Frame> frames)
    {
        var creator = new CreateNewSagaFrame(SagaType);
        frames.Add(creator);

        foreach (var startingCall in StartingCalls)
        {
            frames.Add(startingCall);
            foreach (var frame in startingCall.Creates.SelectMany(x => x.ReturnAction(this).Frames()))
                frames.Add(frame);
        }

        var ifNotCompleted = buildFrameForConditionalInsert(creator.Saga, frameProvider, container);
        frames.Add(ifNotCompleted);
    }

    internal IEnumerable<Frame> DetermineSagaDoesNotExistSteps(Variable sagaId, Variable saga,
        IPersistenceFrameProvider frameProvider, IContainer container)
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
                    foreach (var frame in create.ReturnAction(this).Frames()) yield return frame;
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
                foreach (var frame in call.Creates.SelectMany(x => x.ReturnAction(this).Frames())) yield return frame;
            }
        }
        else
        {
            yield return new AssertSagaStateExistsFrame(saga, sagaId);
        }
    }

    private static Frame buildFrameForConditionalInsert(Variable saga, IPersistenceFrameProvider frameProvider,
        IContainer container)
    {
        var insert = frameProvider.DetermineInsertFrame(saga, container);
        var commit = frameProvider.CommitUnitOfWorkFrame(saga, container);
        return new ConditionalSagaInsertFrame(saga, insert, commit);
    }

    internal IEnumerable<Frame> DetermineSagaExistsSteps(Variable sagaId, Variable saga,
        IPersistenceFrameProvider frameProvider, IContainer container)
    {
        foreach (var call in ExistingCalls)
        {
            yield return call;
            foreach (var frame in call.Creates.SelectMany(x => x.ReturnAction(this).Frames())) yield return frame;
        }

        var update = frameProvider.DetermineUpdateFrame(saga, container);
        var delete = frameProvider.DetermineDeleteFrame(sagaId, saga, container);

        yield return new SagaStoreOrDeleteFrame(saga, update, delete);

        yield return frameProvider.CommitUnitOfWorkFrame(saga, container);
    }
}