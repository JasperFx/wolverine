using System.Diagnostics;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using System.Reflection;
using Wolverine.Configuration;
using Wolverine.Logging;
using Wolverine.Runtime.Handlers;
using Wolverine.Transports.Local;

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

    /// <summary>
    /// Determines whether a type is a valid saga identity type. Supports the standard
    /// primitive types (Guid, int, long, string) as well as strong-typed identifier types
    /// (e.g., OrderId wrapping a Guid).
    /// </summary>
    public static bool IsValidSagaIdType(Type type)
    {
        if (ValidSagaIdTypes.Contains(type)) return true;

        // Accept strong-typed identifiers: value types (structs) or reference types
        // that are not one of the known primitives. Marten and other persistence
        // providers know how to resolve their underlying storage type.
        return type is { IsPrimitive: false, IsEnum: false };
    }

    public SagaChain(WolverineOptions options, IGrouping<Type, HandlerCall> grouping, HandlerGraph parent) : base(options, grouping, parent)
    {
        // After base constructor, saga handlers may have been moved to ByEndpoint (Separated mode).
        // Check what's left in Handlers (not the original grouping).
        var allSagaHandlers = Handlers.Where(x => x.HandlerType.CanBeCastTo<Saga>()).ToArray();
        var distinctSagaTypes = allSagaHandlers.DistinctBy(x => x.HandlerType).ToArray();

        if (distinctSagaTypes.Length == 0)
        {
            // All sagas were separated into ByEndpoint chains — this parent is routing-only.
            var anySaga = grouping.First(x => x.HandlerType.CanBeCastTo<Saga>());
            SagaType = anySaga.HandlerType;
            return;
        }

        try
        {
            var saga = distinctSagaTypes.Single();
            SagaType = saga.HandlerType;
            SagaMethodInfo = saga.Method;

            // Pass ALL saga handler methods so [SagaIdentityFrom] is found regardless of declaration order
            SagaIdMember = DetermineSagaIdMember(MessageType, SagaType,
                allSagaHandlers.Select(x => x.Method).ToArray());

            // Automatically audit the saga id
            if (SagaIdMember != null && AuditedMembers.All(x => x.Member != SagaIdMember))
            {
                AuditedMembers.Add(new AuditedMember(SagaIdMember, SagaIdMember.Name, SagaIdMember.Name));
            }
        }
        catch (Exception e)
        {
            var handlerTypes = distinctSagaTypes
                .Select(x => x.HandlerType).Select(x => x.FullNameInCode()).Join(", ");

            throw new InvalidSagaException(
                $"Command types cannot be handled by multiple saga types. Message {MessageType.FullNameInCode()} is handled by sagas {handlerTypes}",
                e);
        }
    }

    protected override void maybeAssignStickyHandlers(WolverineOptions options, IGrouping<Type, HandlerCall> grouping)
    {
        var notSaga = grouping.Where(x => !x.HandlerType.CanBeCastTo<Saga>());
        foreach (var handlerCall in notSaga)
        {
            tryAssignStickyEndpoints(handlerCall, options);
        }

        var groupedSagas = grouping.Where(x => x.HandlerType.CanBeCastTo<Saga>())
            .GroupBy(x => x.HandlerType).ToArray();

        if (groupedSagas.Length > 1)
        {
            if (options.MultipleHandlerBehavior != MultipleHandlerBehavior.Separated)
            {
                var sagaTypes = groupedSagas.Select(x => x.Key.FullNameInCode()).Join(", ");
                throw new InvalidSagaException(
                    $"Multiple saga types ({sagaTypes}) handle message {MessageType.FullNameInCode()}. " +
                    $"Set MultipleHandlerBehavior to Separated to allow this.");
            }

            // In Separated mode, create a separate SagaChain per saga type
            foreach (var sagaGroup in groupedSagas)
            {
                var sagaCalls = sagaGroup.ToArray();
                var sagaType = sagaGroup.Key;

                var endpoint = options.Transports.GetOrCreate<LocalTransport>()
                    .QueueFor(sagaType.FullNameInCode().ToLowerInvariant());

                var chain = new SagaChain(sagaCalls, options.HandlerGraph, [endpoint]);

                foreach (var call in sagaCalls)
                {
                    Handlers.Remove(call);
                }

                _byEndpoint.Add(chain);
            }
        }
    }

    public SagaChain(HandlerCall handlerCall, HandlerGraph handlerGraph, Endpoint[] endpoints) : base(handlerCall, handlerGraph)
    {
        foreach (var endpoint in endpoints) RegisterEndpoint(endpoint);

        var saga = handlerCall;
        SagaType = saga.HandlerType;
        SagaMethodInfo = saga.Method;

        SagaIdMember = DetermineSagaIdMember(MessageType, SagaType, [saga.Method]);

        // Automatically audit the saga id
        if (SagaIdMember != null && AuditedMembers.All(x => x.Member != SagaIdMember))
        {
            AuditedMembers.Add(new AuditedMember(SagaIdMember, SagaIdMember.Name, SagaIdMember.Name));
        }
    }

    internal SagaChain(HandlerCall[] sagaCalls, HandlerGraph handlerGraph, Endpoint[] endpoints)
        : base(sagaCalls[0].Method.MessageType()!, handlerGraph)
    {
        foreach (var endpoint in endpoints) RegisterEndpoint(endpoint);
        foreach (var call in sagaCalls) Handlers.Add(call);

        var saga = sagaCalls.First();
        SagaType = saga.HandlerType;
        SagaMethodInfo = saga.Method;

        // Pass ALL saga handler methods so [SagaIdentityFrom] is found regardless of declaration order
        SagaIdMember = DetermineSagaIdMember(MessageType, SagaType,
            sagaCalls.Select(x => x.Method).ToArray());

        if (SagaIdMember != null && AuditedMembers.All(x => x.Member != SagaIdMember))
        {
            AuditedMembers.Add(new AuditedMember(SagaIdMember, SagaIdMember.Name, SagaIdMember.Name));
        }

        TypeName = saga.HandlerType.ToSuffixedTypeName(HandlerSuffix).Replace("[]", "Array");
    }

    public override bool TryInferMessageIdentity(out PropertyInfo? property)
    {
        property = SagaIdMember as PropertyInfo;
        return property != null;
    }

    protected override void validateAgainstInvalidSagaMethods(IGrouping<Type, HandlerCall> grouping)
    {
        // Nothing
    }

    public Type SagaType { get; }

    public MethodInfo? SagaMethodInfo { get; set; }

    public MemberInfo? SagaIdMember { get; set; }

    public MethodCall[] ExistingCalls { get; set; } = [];

    public MethodCall[] StartingCalls { get; set; } = [];

    public MethodCall[] NotFoundCalls { get; set; } = [];

    public static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType, MethodInfo? sagaHandlerMethod = null)
    {
        return DetermineSagaIdMember(messageType, sagaType,
            sagaHandlerMethod != null ? [sagaHandlerMethod] : null);
    }

    public static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType, MethodInfo[]? sagaHandlerMethods)
    {
        var expectedSagaIdName = $"{sagaType.Name}Id";

        // Scan ALL handler methods for [SagaIdentityFrom], not just the first one.
        // This fixes the bug where declaration order of NotFound vs Handle matters.
        var specifiedSagaIdMemberName = sagaHandlerMethods?
            .SelectMany(m => m.GetParameters())
            .Select(x => x.GetCustomAttribute<SagaIdentityFromAttribute>())
            .FirstOrDefault(a => a != null)?.PropertyName;

        var members = messageType.GetFields().OfType<MemberInfo>().Concat(messageType.GetProperties()).ToArray();
        return members.FirstOrDefault(x => x.HasAttribute<SagaIdentityAttribute>())
               ?? members.FirstOrDefault(x => x.Name == (specifiedSagaIdMemberName ?? expectedSagaIdName))
               ?? members.FirstOrDefault(x => x.Name == expectedSagaIdName.Replace("Saga", "", StringComparison.InvariantCultureIgnoreCase))
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

        if (AuditedMembers.Count != 0)
        {
            Middleware.Insert(0, new AuditToActivityFrame(this));
        }

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
            generateCodeForMaybeExisting(container, frameProvider, list, messageVariable);
        }

        // .Concat(handlerReturnValueFrames)

        return Middleware.Concat(container.TryCreateConstructorFrames(Handlers)).Concat(list).Concat(Postprocessors).ToList();
    }

    private void generateCodeForMaybeExisting(IServiceContainer container, IPersistenceFrameProvider frameProvider,
        List<Frame> frames, MessageVariable? messageVariable = null)
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
        var existingFrames = DetermineSagaExistsSteps(sagaId, saga, frameProvider, container, messageVariable).ToArray();
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

            if (SagaIdMember != null)
            {
                frames.Add(new SetSagaIdFromSagaFrame(MessageType, SagaIdMember, SagaType));
            }

            // Emit return action frames for non-saga created variables (e.g., cascading messages).
            // Skip the saga variable — its persistence is handled by the conditional insert below
            // which respects IsCompleted(). See GH-2073.
            foreach (var frame in startingCall.Creates
                         .Where(x => x.VariableType != SagaType)
                         .SelectMany(x => x.ReturnAction(this).Frames()))
                frames.Add(frame);
        }

        var ifNotCompleted = buildFrameForConditionalInsert(sagaVariable, frameProvider, container);
        frames.Add(ifNotCompleted);
    }

    internal override bool HasDefaultNonStickyHandlers() => Handlers.Any();

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

            yield return new SetSagaIdFrame(sagaId, SagaType);

            foreach (var call in StartingCalls)
            {
                yield return call;
                // Skip saga-type creates — persistence is handled by the conditional insert below.
                // See GH-2073.
                foreach (var create in call.Creates.Where(x => x.VariableType != SagaType))
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
        IPersistenceFrameProvider frameProvider, IServiceContainer container, MessageVariable? messageVariable = null)
    {
        // Set the saga ID on the context so cascading messages have the correct saga ID
        yield return new SetSagaIdFrame(sagaId, SagaType);

        var handlerFrames = new List<Frame>();
        foreach (var call in ExistingCalls)
        {
            handlerFrames.Add(call);
            foreach (var frame in call.Creates.SelectMany(x => x.ReturnAction(this).Frames())) handlerFrames.Add(frame);
        }

        // For ResequencerSaga<T>, wrap handler calls with ShouldProceed() guard
        if (messageVariable != null && IsResequencerGuarded)
        {
            yield return new ShouldProceedGuardFrame(saga, messageVariable, handlerFrames.ToArray());
        }
        else
        {
            foreach (var frame in handlerFrames) yield return frame;
        }

        var update = frameProvider.DetermineUpdateFrame(saga, container);
        var delete = frameProvider.DetermineDeleteFrame(sagaId, saga, container);

        yield return new SagaStoreOrDeleteFrame(saga, update, delete);

        yield return frameProvider.CommitUnitOfWorkFrame(saga, container);
    }

    private bool IsResequencerGuarded
    {
        get
        {
            var resequencerMessageType = GetResequencerMessageType();
            return resequencerMessageType != null && MessageType.CanBeCastTo(resequencerMessageType);
        }
    }

    private Type? GetResequencerMessageType()
    {
        var current = SagaType;
        while (current != null)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(ResequencerSaga<>))
                return current.GetGenericArguments()[0];
            current = current.BaseType;
        }
        return null;
    }
}
