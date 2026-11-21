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

public partial class SagaChain : HandlerChain
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

        // GH-5702. The same for Saga.Version, which the saga stores check for optimistic concurrency
        AssertVersionIsNotHidden(SagaType);

        _options = options;
    }

    public SagaChain(HandlerCall handlerCall, HandlerGraph handlerGraph, Endpoint[] endpoints) : base(handlerCall, handlerGraph)
    {
        foreach (var endpoint in endpoints) RegisterEndpoint(endpoint);

        var saga = handlerCall;
        SagaType = saga.HandlerType;
        SagaMethodInfo = saga.Method;

        AssertVersionIsNotHidden(SagaType);

        SagaIdMember = DetermineSagaIdMember(MessageType, SagaType, [saga.Method]);

        // Automatically audit the saga id
        if (SagaIdMember != null && AuditedMembers.All(x => x.Member != SagaIdMember))
        {
            AuditedMembers.Add(new AuditedMember(SagaIdMember, SagaIdMember.Name, SagaIdMember.Name));
        }
    }

    /// <summary>
    /// GH-5702. Refuse a saga type that declares its own <c>Version</c>. It hides <see cref="Saga.Version" />, the
    /// revision the saga stores read and bump for their optimistic concurrency check, so the check would quietly stop
    /// working.
    /// </summary>
    [UnconditionalSuppressMessage("Trimming", "IL2070",
        Justification = "Saga types come from handler discovery, which roots them. A trimmed member could not hide Version anyway.")]
    [UnconditionalSuppressMessage("Trimming", "IL2075",
        Justification = "Walks the saga type's base classes up to Saga. Saga types come from handler discovery, which roots them.")]
    internal static void AssertVersionIsNotHidden(Type sagaType)
    {
        for (var type = sagaType; type != null && type != typeof(Saga); type = type.BaseType)
        {
            var hides = type.GetMember(nameof(Saga.Version), MemberTypes.Property | MemberTypes.Field,
                    BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly)
                .Length > 0;

            if (hides)
            {
                throw new InvalidSagaException(
                    $"Saga type {sagaType.FullNameInCode()} declares its own {nameof(Saga.Version)}" +
                    (type == sagaType ? "" : $" (on {type.FullNameInCode()})") +
                    $", which hides {nameof(Saga)}.{nameof(Saga.Version)}, the revision Wolverine's saga storage uses to check for concurrent updates. " +
                    $"Remove it: {nameof(Saga)}.{nameof(Saga.Version)} already does that job, and a saga that implements JasperFx.IRevisioned needs nothing of its own. " +
                    "Or, if it meant something else, rename it.");
            }
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
    /// GH-5556. The saga's Compensate methods for the T of this chain's Fault&lt;T&gt;
    /// </summary>
    public MethodCall[] CompensateCalls { get; set; } = [];

    /// <summary>
    /// GH-5572. How long completed instances of this chain's saga type are kept, or null when they are deleted on
    /// completion. Known once the chain has generated its code.
    /// </summary>
    public TimeSpan? CompletedSagaRetention { get; private set; }

    private bool retainsCompletedSagas => CompletedSagaRetention.HasValue;

    /// <summary>
    /// GH-5665. Whether this chain records each message in its saga's history (<see cref="RecordSagaHistoryAttribute" />).
    /// Known once the chain has generated its code.
    /// </summary>
    public bool RecordsHistory { get; private set; }

    private Frame? recordHistory(Variable? saga, Variable? sagaId, IEnumerable<MethodCall> calls, SagaHistoryStep step)
    {
        if (!RecordsHistory) return null;

        var methods = calls.Select(x => x.Method.Name).Distinct().ToArray();
        var handler = $"{SagaType.Name}.{(methods.Length == 0 ? step.ToString() : string.Join("/", methods))}";

        return new RecordSagaHistoryFrame(SagaType, saga, sagaId, DetermineSagaIdMember(SagaType, SagaType), handler,
            step, retainsCompletedSagas);
    }

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
            .Concat(CompensateCalls)
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
        _options ??= container.Services.GetService(typeof(WolverineOptions)) as WolverineOptions;

        // GH-5665
        RecordsHistory = !IsEventSourced &&
                         SagaHistory.IsRecorded(SagaType,
                             container.Services.GetService(typeof(WolverineOptions)) as WolverineOptions);

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

        CompensateCalls = findByNames(SagaCompensations.Compensate);
        if (CompensateCalls.Length != 0 && ExistingCalls.Length != 0)
        {
            throw new InvalidSagaException(
                $"{SagaType.FullNameInCode()} both handles {MessageType.FullNameInCode()} and declares Compensate methods for its message. Use one or the other");
        }

        var statics = ExistingCalls.Where(x => x.Method.IsStatic);
        if (statics.Any())
        {
            throw new InvalidSagaException(
                $"It is not legal to use static methods to operate on existing sagas. Use NotFound() for handling non-existent sagas for the identity");
        }

        Handlers.Clear();

        // GH-5415: an event-sourced saga is its own stream
        if (IsEventSourced)
        {
            return determineEventSourcedFrames(rules, container, frameProvider);
        }

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

    private WolverineOptions? _options;

    /// <summary>
    /// GH-5674. What this chain does with its message when the saga is missing and there is no <c>NotFound</c> for it:
    /// <see cref="DiscardIfSagaMissingAttribute" /> on the message type or on the saga's method for it, then
    /// <see cref="SagaMissingAttribute" /> on the saga type, then <c>opts.Policies.WhenSagaIsMissing()</c>. A saga's
    /// compensation chain (its <c>Fault&lt;T&gt;</c> chain) reports a missing saga, so the fault is dead lettered
    /// tagged <c>compensation-saga-missing</c>, unless the saga itself says <c>[SagaMissing(Discard)]</c> (GH-5709)
    /// </summary>
    internal SagaMissingBehavior DetermineSagaMissingBehavior() => DetermineSagaMissingBehavior(_options);

    /// <inheritdoc cref="DetermineSagaMissingBehavior()" />
    internal SagaMissingBehavior DetermineSagaMissingBehavior(WolverineOptions? options)
    {
        if (CompensateCalls.Length != 0 || SagaCompensations.FaultMessageType(MessageType) != null)
        {
            // GH-5709: only the saga's own [SagaMissing] covers its faults. [DiscardIfSagaMissing] and the global
            // policy are about the saga's ordinary messages
            return SagaType.TryGetAttribute<SagaMissingAttribute>(out var forFaults)
                ? forFaults.Behavior
                : SagaMissingBehavior.Throw;
        }

        // GH-5673: a reply about an undo message for a saga that has gone has nothing left to unwind
        if (IsUndoReplyChain) return SagaMissingBehavior.Discard;

        if (MessageType.HasAttribute<DiscardIfSagaMissingAttribute>() ||
            SagaHandlerCalls().Any(x => x.Method.HasAttribute<DiscardIfSagaMissingAttribute>()))
        {
            return SagaMissingBehavior.Discard;
        }

        if (SagaType.TryGetAttribute<SagaMissingAttribute>(out var sagaLevel)) return sagaLevel.Behavior;

        return options?.SagaMissingDefault ?? SagaMissingBehavior.Throw;
    }

    // GH-5674: no NotFound and no Start for the message, and no saga to hand it to
    private Frame whenTheSagaIsMissing(Variable saga, Variable sagaId)
        => DetermineSagaMissingBehavior(_options) == SagaMissingBehavior.Discard
            ? new DiscardMessageForMissingSagaFrame(SagaType, sagaId)
            : new AssertSagaStateExistsFrame(saga, sagaId);

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
            var completedFrames = DetermineRetainedSagaCompletedSteps(sagaId, saga, frameProvider, container).ToArray();
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
                DetermineRetainedSagaCompletedSteps(guard.SagaId, guard.Saga, frameProvider, container).ToArray(), existingFrames)
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

        // GH-5673: the undo entries the new saga pushed are tied to the steps it sent
        if (tracksUndoSteps) frames.Add(SagaUndoFrame.After(sagaVariable));

        // GH-5674: the latest id of each replacing timeout the new saga scheduled
        if (usesReplacingTimeouts) frames.Add(new RecordSagaTimeoutsFrame(sagaVariable, false));
        // GH-5665
        if (recordHistory(sagaVariable, null, StartingCalls, SagaHistoryStep.Start) is { } record) frames.Add(record);

        var ifNotCompleted = buildFrameForConditionalInsert(sagaVariable, frameProvider, container, retainsCompletedSagas);
        frames.Add(ifNotCompleted);
    }

    // GH-5673: this chain is a compensating saga's sequential-unwind replies
    internal bool IsUndoReplyChain => MessageType.IsGenericType && MessageType.GetGenericTypeDefinition() == typeof(UndoReply<>);

    // GH-5673: the saga's handlers push undo messages and confirm them with their steps' replies. Not its compensation
    // chains, nor its unwind's own replies
    private bool tracksUndoSteps => SagaType.CanBeCastTo<CompensatingSaga>() && !IsUndoReplyChain &&
                                    SagaCompensations.FaultMessageType(MessageType) == null;

    // GH-5674: does this chain's saga handle a timeout that replaces earlier ones?
    private bool usesReplacingTimeouts => SagaTimeouts.SagaUsesReplacingTimeouts(SagaType);

    /// <summary>
    /// GH-5572. What happens to a message for a retained saga that has already completed: the same as for a saga
    /// that does not exist, except that it never starts a new saga over the completed one, which keeps its id.
    /// </summary>
    internal IEnumerable<Frame> DetermineRetainedSagaCompletedSteps(Variable sagaId, Variable saga,
        IPersistenceFrameProvider frameProvider, IServiceContainer container)
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

            // GH-5665: the retained saga is still there, so the late message joins its history. Nothing else is saved on
            // this branch, so the unit of work is committed for the row, as the other branches commit it with the saga
            if (recordHistory(saga, sagaId, NotFoundCalls, SagaHistoryStep.NotFound) is { } record)
            {
                yield return record;
                yield return frameProvider.CommitUnitOfWorkFrame(saga, container);
            }
        }
        else if (StartingCalls.Length != 0)
        {
            yield return new SkipStartOfCompletedSagaFrame(SagaType, sagaId);
            yield return new ReturnFrame();
        }
        else
        {
            yield return whenTheSagaIsMissing(saga, sagaId);
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

            if (tracksUndoSteps) yield return SagaUndoFrame.After(saga);
            if (usesReplacingTimeouts) yield return new RecordSagaTimeoutsFrame(saga, false);
            // GH-5665
            if (recordHistory(saga, sagaId, StartingCalls, SagaHistoryStep.Start) is { } record) yield return record;

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
            yield return whenTheSagaIsMissing(saga, sagaId);
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

        // GH-5556: the saga's Compensate methods for the failed message this Fault<T> carries
        if (CompensateCalls.Length != 0)
        {
            handlerFrames.Add(new CompensateSagaFrame(saga, MessageType));
        }

        // GH-5673: a reply about an undo message of the saga's sequential unwind
        if (IsUndoReplyChain)
        {
            handlerFrames.Add(new ContinueUnwindFrame(saga, MessageType, false));
        }

        var body = new List<Frame>();

        // GH-5673: a step's reply confirms the undo entries pushed before the step
        if (tracksUndoSteps) body.Add(SagaUndoFrame.Before(saga));

        // For ResequencerSaga<T>, wrap handler calls with ShouldProceed() guard
        if (messageVariable != null && IsResequencerGuarded)
        {
            body.Add(new ShouldProceedGuardFrame(saga, messageVariable, handlerFrames.ToArray()));
        }
        else
        {
            body.AddRange(handlerFrames);
        }

        // GH-5673: the undo entries the handler pushed are tied to the steps it sent
        if (tracksUndoSteps) body.Add(SagaUndoFrame.After(saga));

        // GH-5674: the latest id of each replacing timeout the handler scheduled
        if (usesReplacingTimeouts) body.Add(new RecordSagaTimeoutsFrame(saga, false));
        // GH-5665: recorded before the saga is saved, so the row rides the saga's own transaction
        var historyStep = CompensateCalls.Length != 0 ? SagaHistoryStep.Compensate : SagaHistoryStep.Continue;
        if (recordHistory(saga, sagaId, ExistingCalls.Concat(CompensateCalls), historyStep) is { } record)
        {
            body.Add(record);
        }

        var update = frameProvider.DetermineUpdateFrame(saga, container);
        var delete = frameProvider.DetermineDeleteFrame(sagaId, saga, container);

        body.Add(new SagaStoreOrDeleteFrame(saga, update, delete, retainsCompletedSagas));

        body.Add(frameProvider.CommitUnitOfWorkFrame(saga, container));

        // GH-5674: a timeout a later one replaced reaches neither the saga's methods nor its storage
        if (SagaTimeouts.Replaces(MessageType))
        {
            yield return new SupersededTimeoutGuardFrame(saga, body.ToArray());
        }
        else
        {
            foreach (var frame in body) yield return frame;
        }

        if (usesReplacingTimeouts) yield return new CancelSupersededTimeoutsFrame();
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
