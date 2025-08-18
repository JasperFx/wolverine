using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Runtime;
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
    public static readonly Type[] ValidSagaIdTypes = [typeof(Guid), typeof(int), typeof(long), typeof(string)];

    public SagaChain(WolverineOptions options, IGrouping<Type, HandlerCall> grouping, HandlerGraph parent) : base(options, grouping, parent)
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

    protected override void validateAgainstInvalidSagaMethods(IGrouping<Type, HandlerCall> grouping)
    {
        // Nothing
    }

    protected override void tryAssignStickyEndpoints(HandlerCall handlerCall, WolverineOptions options)
    {
        // nope, don't do this with saga chains 
    }

    public Type SagaType { get; }

    public MemberInfo? SagaIdMember { get; set; }

    public MethodCall[] ExistingCalls { get; set; } = [];

    public MethodCall[] StartingCalls { get; set; } = [];

    public MethodCall[] NotFoundCalls { get; set; } = [];

    public static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType)
    {
        var expectedSagaIdName = $"{sagaType.Name}Id";

        var members = messageType.GetFields().OfType<MemberInfo>().Concat(messageType.GetProperties()).ToArray();
        return members.FirstOrDefault(x => x.HasAttribute<SagaIdentityAttribute>())
               ?? members.FirstOrDefault(x => x.Name == expectedSagaIdName)
               ?? members.FirstOrDefault(x => x.Name == SagaIdMemberName) ??
               members.FirstOrDefault(x => x.Name.EqualsIgnoreCase("Id"));
    }

    private MethodCall[] findByNames(params string[] methodNames)
    {
        return Handlers.Where(x => methodNames.Contains(x.Method.Name) && x.HandlerType.CanBeCastTo<Saga>()).ToArray();
    }

    internal override List<Frame> DetermineFrames(GenerationRules rules, IServiceContainer container,
        MessageVariable messageVariable)
    {
        applyCustomizations(rules, container);

        var frameProvider = rules.GetPersistenceProviders(this, container);
        
        frameProvider.ApplyTransactionSupport(this, container);

        NotFoundCalls = findByNames(NotFound);
        StartingCalls = findByNames(Start, Starts, StartOrHandle, StartsOrHandles);

        ExistingCalls = findByNames(Orchestrate, Orchestrates, StartOrHandle, StartsOrHandles, Handle, Handles,
            Consume, Consumes);

        var statics = ExistingCalls.Where(x => x.Method.IsStatic);
        if (statics.Any())
        {
            throw new InvalidSagaException(
                $"It is not legal to use static methods to operate on existing sagas. Use NotFound() for handling non-existent sagas for the identity");
        }

        Handlers.Clear();

        var list = new List<Frame>();

        if (ExistingCalls.Length == 0)
        {
            generateForOnlyStartingSaga(container, frameProvider, list);
        }
        else
        {
            generateCodeForMaybeExisting(container, frameProvider, list);
        }

// .Concat(handlerReturnValueFrames)

        return Middleware.Concat(container.TryCreateConstructorFrames(Handlers)).Concat(list).Concat(Postprocessors).ToList();
    }

    private void generateCodeForMaybeExisting(IServiceContainer container, IPersistenceFrameProvider frameProvider,
        List<Frame> frames)
    {
        var findSagaId = SagaIdMember == null
            ? (Frame)new PullSagaIdFromEnvelopeFrame(frameProvider.DetermineSagaIdType(SagaType, container))
            : new PullSagaIdFromMessageFrame(MessageType, SagaIdMember);


        var load = frameProvider.DetermineLoadFrame(container, SagaType, findSagaId.Creates.First());

        // Using this one frame to tie everything together
        var resolve = new ResolveSagaFrame(findSagaId, load);
        frames.Add(resolve);
        var saga = resolve.Saga;
        var sagaId = resolve.SagaId;

        var startingFrames = DetermineSagaDoesNotExistSteps(sagaId, saga, frameProvider, container).ToArray();
        var existingFrames = DetermineSagaExistsSteps(sagaId, saga, frameProvider, container).ToArray();
        var ifNullBlock = new IfElseNullGuardFrame(saga, startingFrames,
            existingFrames);

        frames.Add(ifNullBlock);
    }

    private void generateForOnlyStartingSaga(IServiceContainer container, IPersistenceFrameProvider frameProvider,
        List<Frame> frames)
    {
        var sagaVariable = StartingCalls.SelectMany(x => x.Creates).FirstOrDefault(x => x.VariableType == SagaType);
        if (sagaVariable == null)
        {
            var creator = new CreateNewSagaFrame(SagaType);
            frames.Add(creator);
            sagaVariable = creator.Saga;
        }

        foreach (var startingCall in StartingCalls)
        {
            frames.Add(startingCall);
            foreach (var frame in startingCall.Creates.SelectMany(x => x.ReturnAction(this).Frames()))
                frames.Add(frame);
        }

        if (sagaVariable.ReturnAction(this).Frames().OfType<ISagaOperation>()
            .Any(x => x.Saga == sagaVariable && x.Operation == SagaOperationType.InsertAsync))
        {
            return;
        }
        
        var ifNotCompleted = buildFrameForConditionalInsert(sagaVariable, frameProvider, container);
        frames.Add(ifNotCompleted);
    }

    // Always true!
    internal override bool HasDefaultNonStickyHandlers() => true;

    internal IEnumerable<Frame> DetermineSagaDoesNotExistSteps(Variable sagaId, Variable saga,
        IPersistenceFrameProvider frameProvider, IServiceContainer container)
    {
        if (MessageType.CanBeCastTo<TimeoutMessage>())
        {
            yield return new ReturnFrame();
            yield break;
        }

        if (StartingCalls.Length != 0)
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

    private static Frame buildFrameForConditionalInsert(Variable saga, IPersistenceFrameProvider frameProvider,
        IServiceContainer container)
    {
        var insert = frameProvider.DetermineInsertFrame(saga, container);
        var commit = frameProvider.CommitUnitOfWorkFrame(saga, container);
        return new ConditionalSagaInsertFrame(saga, insert, commit);
    }

    internal IEnumerable<Frame> DetermineSagaExistsSteps(Variable sagaId, Variable saga,
        IPersistenceFrameProvider frameProvider, IServiceContainer container)
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