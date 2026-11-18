using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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

    /// <summary>
    /// GH-4484. A static method a saga declares for a message that reaches it with no saga id -- on the message or in
    /// the saga-id envelope header -- instead of the message failing with <see cref="IndeterminateSagaStateIdException" />
    /// </summary>
    public const string IdNotProvided = "IdNotProvided";

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
        // GH-5136. HandlerGraph builds one chain per (message type, handler type), so a saga chain holds the
        // methods of exactly one saga type and nothing else
        var allSagaHandlers = Handlers.Where(x => x.HandlerType.CanBeCastTo<Saga>()).ToArray();
        var distinctSagaTypes = allSagaHandlers.DistinctBy(x => x.HandlerType).ToArray();

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

        // GH-5572. Refused here, while the handler graph is built at startup, rather than when the chain first
        // generates its code
        SagaRetention.AssertCompletedAtIsNotHidden(SagaType, options);
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

    /// <summary>
    /// GH-4484. The saga's IdNotProvided methods for this message type
    /// </summary>
    public MethodCall[] IdNotProvidedCalls { get; set; } = [];

    /// <summary>
    /// GH-5572. How long completed instances of this chain's saga type are kept, or null when they are deleted on
    /// completion. Known once the chain has generated its code.
    /// </summary>
    public TimeSpan? CompletedSagaRetention { get; private set; }

    private bool retainsCompletedSagas => CompletedSagaRetention.HasValue;

    /// <summary>
    /// GH-5331. Every saga handler method on this chain, whether or not the chain has compiled.
    /// DetermineFrames sorts the saga calls into StartingCalls / ExistingCalls / NotFoundCalls and then
    /// clears Handlers, so anything describing the saga after the first message was handled has to read
    /// the sorted calls as well
    /// </summary>
    internal MethodCall[] SagaHandlerCalls()
    {
        return Handlers
            .Where(x => x.HandlerType.CanBeCastTo<Saga>())
            .OfType<MethodCall>()
            .Concat(StartingCalls)
            .Concat(ExistingCalls)
            .Concat(NotFoundCalls)
            .Concat(IdNotProvidedCalls)
            .Distinct()
            .ToArray();
    }

    public static MemberInfo? DetermineSagaIdMember(Type messageType, Type sagaType, MethodInfo? sagaHandlerMethod = null)
    {
        return DetermineSagaIdMember(messageType, sagaType,
            sagaHandlerMethod != null ? [sagaHandlerMethod] : null);
    }

    // GetFields() + GetProperties() walk over a runtime-resolved messageType.
    // Saga handlers are an opt-in feature; the messageType is the user's command/
    // event, statically rooted by handler discovery. Same chunk G / chunk K
    // rationale: leaf suppression here keeps the IPersistenceFrameProvider /
    // SagaChain surface free of [Requires*] cascade. AOT-clean apps preserve
    // saga-message types via [DynamicallyAccessedMembers(PublicFields|PublicProperties)]
    // on the message type or by registering them through the source-generated
    // handler discovery path.
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Saga handlers are opt-in; user-supplied saga-message types are statically rooted via handler discovery. See AOT guide.")]
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
        // Match either the bare name (e.g. "Start") or its async-suffixed twin
        // (e.g. "StartAsync"). HandlerDiscovery already strips the "Async"
        // suffix when picking up handler methods, so without this the saga
        // method would be discovered into the chain but silently dropped from
        // StartingCalls / ExistingCalls / NotFoundCalls and never invoked.
        // See https://github.com/JasperFx/wolverine/issues/2578 and the
        // re-report https://github.com/JasperFx/wolverine/issues/3274.
        return Handlers
            .Where(x => x.HandlerType.CanBeCastTo<Saga>()
                        && methodNames.Any(n =>
                            x.Method.Name == n || x.Method.Name == n + "Async"))
            .ToArray();
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

        CompletedSagaRetention = determineRetention(frameProvider, container);

        NotFoundCalls = findByNames(NotFound);
        IdNotProvidedCalls = findByNames(IdNotProvided);

        var instanceIdNotProvided = IdNotProvidedCalls.Where(x => !x.Method.IsStatic).ToArray();
        if (instanceIdNotProvided.Length != 0)
        {
            throw new InvalidSagaException(
                $"{SagaType.FullNameInCode()}.{instanceIdNotProvided[0].Method.Name}() must be static: there is no saga to call it on when the message carries no saga id");
        }
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

        // GH-5553: a saga that declares only NotFound or IdNotProvided for a message -- NotFound(Fault<T>) is the usual
        // one -- loads the saga like any other. Treating it as a starting message created an empty saga
        if (ExistingCalls.Length == 0 && StartingCalls.Length != 0)
        {
            generateForOnlyStartingSaga(container, frameProvider, list);
        }
        else
        {
            generateCodeForMaybeExisting(container, frameProvider, list, messageVariable);
        }

        // .Concat(handlerReturnValueFrames)

        return Middleware.Concat(container.TryCreateConstructorFrames(Handlers)).Concat(list).Concat(Postprocessors)
            .Concat(PostCommitPostprocessors) // GH-3975
            .ToList();
    }

    private TimeSpan? determineRetention(IPersistenceFrameProvider frameProvider, IServiceContainer container)
    {
        var options = container.Services.GetService(typeof(WolverineOptions)) as WolverineOptions;
        SagaRetention.AssertCompletedAtIsNotHidden(SagaType, options);

        var explicitRetention = SagaRetention.ExplicitFor(SagaType);
        var retention = explicitRetention ?? options?.Durability.CompletedSagaRetention;
        if (retention == null) return null;

        switch (frameProvider.SagaRetentionSupportFor(SagaType, container))
        {
            case SagaRetentionSupport.Supported:
                return retention;

            case SagaRetentionSupport.NotSupported when explicitRetention.HasValue:
                throw new InvalidSagaException(
                    $"Saga type {SagaType.FullNameInCode()} is marked with [{nameof(RetainWhenCompletedAttribute)}], but its storage ({frameProvider.GetType().FullNameInCode()}) cannot retain completed sagas. Retention works with the lightweight database saga tables, Marten, Polecat, Fisher, EF Core, RavenDB and CosmosDB.");

            // The global policy only applies where it can, and a saga kept in memory does not outlive the process anyway
            default:
                return null;
        }
    }

    private void generateCodeForMaybeExisting(IServiceContainer container, IPersistenceFrameProvider frameProvider,
        List<Frame> frames, MessageVariable? messageVariable = null)
    {
        var findSagaId = SagaIdMember == null
            ? (Frame)new PullSagaIdFromEnvelopeFrame(SagaType, frameProvider.DetermineSagaIdType(SagaType, this, container))
            : new PullSagaIdFromMessageFrame(MessageType, SagaType, SagaIdMember);


        var load = frameProvider.DetermineLoadFrame(container, SagaType, findSagaId.Creates.First());

        if (IdNotProvidedCalls.Length != 0)
        {
            generateCodeWithIdNotProvided(container, frameProvider, frames, messageVariable, findSagaId, load);
            return;
        }

        // Using this one frame to tie everything together
        var resolve = new ResolveSagaFrame(findSagaId, load);
        frames.Add(resolve);
        var saga = resolve.Saga;
        var sagaId = resolve.SagaId;

        var startingFrames = DetermineSagaDoesNotExistSteps(sagaId, saga, frameProvider, container).ToArray();
        var existingFrames = DetermineSagaExistsSteps(sagaId, saga, frameProvider, container, messageVariable).ToArray();

        if (retainsCompletedSagas)
        {
            // GH-5572: a completed saga of a retained type is still in storage, but must never reach Handle
            var completedFrames = DetermineRetainedSagaCompletedSteps(sagaId, saga).ToArray();
            frames.Add(new RetainedSagaGuardFrame(saga, startingFrames, completedFrames, existingFrames));
            return;
        }

        var ifNullBlock = new IfElseNullGuardFrame(saga, startingFrames,
            existingFrames);

        frames.Add(ifNullBlock);
    }

    // GH-4484: as generateCodeForMaybeExisting, except that a message with no saga id goes to IdNotProvided
    private void generateCodeWithIdNotProvided(IServiceContainer container, IPersistenceFrameProvider frameProvider,
        List<Frame> frames, MessageVariable? messageVariable, Frame findSagaId, Frame load)
    {
        switch (findSagaId)
        {
            case PullSagaIdFromEnvelopeFrame fromEnvelope:
                fromEnvelope.ReportsMissingId = true;
                break;
            case PullSagaIdFromMessageFrame fromMessage:
                fromMessage.ReportsMissingId = true;
                break;
        }

        var idNotProvided = new List<Frame>();
        foreach (var call in IdNotProvidedCalls)
        {
            idNotProvided.Add(call);
            idNotProvided.AddRange(call.Creates.SelectMany(x => x.ReturnAction(this).Frames()));
        }

        var guard = new SagaIdGuardFrame(SagaType, findSagaId, load, idNotProvided.ToArray());

        var startingFrames = DetermineSagaDoesNotExistSteps(guard.SagaId, guard.Saga, frameProvider, container).ToArray();
        var existingFrames = DetermineSagaExistsSteps(guard.SagaId, guard.Saga, frameProvider, container, messageVariable).ToArray();

        // GH-5572: as in generateCodeForMaybeExisting, a retained completed saga never reaches Handle
        guard.WhenTheIdIsKnown(retainsCompletedSagas
            ? new RetainedSagaGuardFrame(guard.Saga, startingFrames,
                DetermineRetainedSagaCompletedSteps(guard.SagaId, guard.Saga).ToArray(), existingFrames)
            : new IfElseNullGuardFrame(guard.Saga, startingFrames, existingFrames));

        frames.Add(guard);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2072",
        Justification = "SagaType originates from a HandlerCall's HandlerType, which is application-rooted via HandlerDiscovery. Saga types are concrete classes with public default constructors (CreateNewSagaFrame enforces this at runtime), preserved by the registration. AOT consumers run pre-generated frames via TypeLoadMode.Static.")]
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

        var ifNotCompleted = buildFrameForConditionalInsert(sagaVariable, frameProvider, container, retainsCompletedSagas);
        frames.Add(ifNotCompleted);
    }

    /// <summary>
    /// GH-5572. What happens to a message for a retained saga that has already completed: the same as for a saga
    /// that does not exist, except that it never starts a new saga over the completed one, which keeps its id.
    /// </summary>
    internal IEnumerable<Frame> DetermineRetainedSagaCompletedSteps(Variable sagaId, Variable saga)
    {
        if (MessageType.CanBeCastTo<TimeoutMessage>())
        {
            yield return new ReturnFrame();
            yield break;
        }

        if (NotFoundCalls.Length != 0)
        {
            // The not-found branch may already use these calls, and a frame can only appear once
            foreach (var original in NotFoundCalls)
            {
                var call = new MethodCall(original.HandlerType, original.Method)
                {
                    CommentText = original.CommentText
                };
                foreach (var alias in original.Aliases) call.Aliases[alias.Key] = alias.Value;

                call.TrySetArgument(sagaId);
                yield return call;
                foreach (var frame in call.Creates.SelectMany(x => x.ReturnAction(this).Frames())) yield return frame;
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

            var ifNotCompleted = buildFrameForConditionalInsert(saga, frameProvider, container, retainsCompletedSagas);
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
        IServiceContainer container, bool retain)
    {
        var insert = frameProvider.DetermineInsertFrame(saga, container);
        var commit = frameProvider.CommitUnitOfWorkFrame(saga, container);
        return new ConditionalSagaInsertFrame(saga, insert, commit, retain);
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

        yield return new SagaStoreOrDeleteFrame(saga, update, delete, retainsCompletedSagas);

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
