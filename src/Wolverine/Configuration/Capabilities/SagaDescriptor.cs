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
}

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
    TypeDescriptor[] PublishedTypes);

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
