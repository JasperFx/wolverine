using JasperFx.Descriptors;

namespace Wolverine.Configuration.Capabilities;

/// <summary>
/// Capability shape for a Wolverine <see cref="Saga"/>. Surfaced through
/// <see cref="ServiceCapabilities.Sagas"/> so external monitoring tools
/// (CritterWatch in particular) can render the saga as a workflow without
/// having to introspect runtime types. One descriptor per concrete saga
/// state class, regardless of how many handler chains feed it.
/// </summary>
/// <remarks>
/// Populated from <see cref="Persistence.Sagas.SagaChain"/> instances on
/// <see cref="WolverineOptions.HandlerGraph"/>. The role classification
/// mirrors the same method-name lookup that <c>SagaChain.DetermineFrames</c>
/// uses internally for code generation, so the static descriptor and the
/// generated runtime behaviour stay in lock-step.
/// </remarks>
public class SagaDescriptor
{
    public SagaDescriptor()
    {
    }

    public SagaDescriptor(TypeDescriptor stateType)
    {
        StateType = stateType;
    }

    /// <summary>The concrete <c>: Saga</c> class.</summary>
    public TypeDescriptor StateType { get; set; } = null!;

    /// <summary>
    /// CLR type of the saga identity (Guid / int / long / string / a
    /// strong-typed identifier), reported as a fully-qualified type name
    /// so the descriptor stays JSON-serialisable. Inferred from the
    /// first message in <see cref="Messages"/> that exposes a saga-id
    /// member; null when Wolverine couldn't determine it (a runtime
    /// error would also be raised in that case, but the descriptor is
    /// still useful for diagnostics).
    /// </summary>
    public string? SagaIdType { get; set; }

    /// <summary>
    /// Every message that touches this saga, with the role each message
    /// plays. Use to render the saga sequence diagram in CritterWatch.
    /// </summary>
    public List<SagaMessageRole> Messages { get; set; } = new();

    /// <summary>
    /// Tag identifying which persistence provider owns this saga at
    /// runtime (e.g. <c>"Marten"</c>, <c>"EntityFrameworkCore"</c>,
    /// <c>"RavenDb"</c>, <c>"InMemory"</c>). Resolved by walking the
    /// registered <see cref="Wolverine.Persistence.IPersistenceFrameProvider"/>
    /// chain — same precedence the saga handler pipeline uses at
    /// code-gen time, so monitoring tools can group sagas by backing
    /// store. Null when the merged-from-storage path (per-storage
    /// <c>ISagaStoreDiagnostics</c>) leaves it unset; callers should
    /// treat null as "unknown" rather than failing.
    /// </summary>
    public string? StorageProvider { get; set; }

    /// <summary>
    /// GH-5415. Whether this saga's storage keeps its history, so a monitoring tool can read its events and its state
    /// at any step through <see cref="Persistence.Sagas.ISagaHistoryDiagnostics" />. True for an event-sourced saga on
    /// Marten, Polecat or Fisher; false for a saga kept as a document or a row.
    /// </summary>
    public bool SupportsHistory { get; set; }

    /// <summary>
    /// GH-5670. Whether this is an <see cref="EventSourcedSaga" />, whose methods append events to the
    /// saga's own stream (<see cref="SagaMethodDescriptor.Events" />) besides sending messages.
    /// </summary>
    public bool IsEventSourced { get; set; }

    /// <summary>
    /// GH-5670. The saga's <c>Compensate</c> methods: which failed message and exception each one compensates, and the
    /// undo messages (and, for an event-sourced saga, the events) it returns.
    /// </summary>
    public List<SagaCompensationDescriptor> Compensations { get; set; } = new();
}

/// <summary>
/// GH-5670. One saga method that handles a message, and what it returns as far as its signature says. A message the
/// method sends through <c>IMessageBus</c> instead of returning it cannot be seen here, and a return type such as
/// <c>OutgoingMessages</c> or <c>IEnumerable&lt;object&gt;</c> only says that more is decided at run time
/// (<see cref="ReturnsMoreAtRunTime" />).
/// </summary>
/// <param name="Name">The method's name: <c>Start</c>, <c>StartOrHandle</c>, <c>Handle</c>, <c>NotFound</c>, ...</param>
/// <param name="Role">
/// What the method does with the saga. <see cref="SagaRole.NotFound" /> runs when no saga matches the message, or the
/// saga has already completed.
/// </param>
/// <param name="Messages">Messages it sends right away.</param>
/// <param name="Scheduled">Messages it schedules for later: a <see cref="TimeoutMessage" /> or a <c>ScheduledMessage&lt;T&gt;</c>.</param>
/// <param name="Events">For an event-sourced saga, the events it appends to the saga's stream.</param>
public record SagaMethodDescriptor(
    string Name,
    SagaRole Role,
    TypeDescriptor[] Messages,
    TypeDescriptor[] Scheduled,
    TypeDescriptor[] Events)
{
    /// <summary>
    /// The method returns something whose contents are only known at run time, such as <c>OutgoingMessages</c>,
    /// <c>IEnumerable&lt;object&gt;</c> or <c>object</c>: messages, or for an event-sourced saga events, that the
    /// signature doesn't name.
    /// </summary>
    public bool ReturnsMoreAtRunTime { get; init; }
}

/// <summary>
/// GH-5670. One <c>Compensate</c> method of a saga: it runs when <paramref name="FailedMessageType" /> fails with
/// <paramref name="ExceptionType" /> (or a subclass) after its retries.
/// </summary>
/// <param name="FailedMessageType">The message whose failure it compensates.</param>
/// <param name="ExceptionType">The exception it matches; <see cref="Exception" /> when it takes any failure.</param>
/// <param name="Method">What the method returns: the undo messages, and for an event-sourced saga, events.</param>
public record SagaCompensationDescriptor(
    TypeDescriptor FailedMessageType,
    TypeDescriptor ExceptionType,
    SagaMethodDescriptor Method);

/// <summary>
/// One incoming message's role inside a saga, plus the messages that
/// handler cascades when it runs.
/// </summary>
/// <param name="MessageType">The message that triggers this handler.</param>
/// <param name="Role">What the handler does with the saga (Start / Orchestrate / etc).</param>
/// <param name="SagaIdMember">
/// Name of the property/field on <paramref name="MessageType"/> that
/// carries the saga identity. Null when Wolverine couldn't infer one —
/// in that case the runtime falls back to pulling the id from the
/// envelope. Per-message because Wolverine's id-resolution rules
/// (<c>[SagaIdentity]</c>, <c>{SagaName}Id</c>, <c>Id</c>, …) frequently
/// produce different property names across the messages that touch the
/// same saga.
/// </param>
/// <param name="PublishedTypes">
/// Cascading message types this handler emits, derived from
/// <c>HandlerChain.PublishedTypes()</c>. Empty when the handler
/// returns no messages.
/// </param>
public record SagaMessageRole(
    TypeDescriptor MessageType,
    SagaRole Role,
    string? SagaIdMember,
    TypeDescriptor[] PublishedTypes)
{
    /// <summary>GH-5670. The message is a <see cref="TimeoutMessage" />, which the saga schedules for itself.</summary>
    public bool IsTimeout { get; init; }

    /// <summary>
    /// GH-5670. Every saga method that handles this message, in the order Wolverine runs them, with what each returns.
    /// A <c>NotFound</c> method beside a <c>Handle</c> is how the saga handles a message that arrives after it completed.
    /// </summary>
    public SagaMethodDescriptor[] Methods { get; init; } = [];
}

/// <summary>
/// What a particular handler does to / for the saga.
///   <list type="bullet">
///     <item><description><b>Start</b> — <c>Start(...)</c> / <c>Starts(...)</c>. Creates a brand-new saga instance.</description></item>
///     <item><description><b>StartOrHandle</b> — <c>StartOrHandle(...)</c> / <c>StartsOrHandles(...)</c>. Creates a saga if none exists for the id, otherwise advances the existing one.</description></item>
///     <item><description><b>Orchestrate</b> — <c>Orchestrate(...)</c> / <c>Handle(...)</c> / <c>Consume(...)</c>. Advances an existing saga; errors if no saga matches the id.</description></item>
///     <item><description><b>NotFound</b> — <c>NotFound(...)</c>. Compensating handler invoked when no saga matches the inbound message's id.</description></item>
///   </list>
/// </summary>
public enum SagaRole
{
    Start,
    StartOrHandle,
    Orchestrate,
    NotFound
}
