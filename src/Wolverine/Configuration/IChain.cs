using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Logging;
using Wolverine.Middleware;
using Wolverine.Persistence;
using Wolverine.Runtime;

namespace Wolverine.Configuration;

internal static class ChainExtensions
{
    public static bool MatchesScope(this IChain chain, MethodInfo method)
    {
        if (chain == null) return true;

        if (method.TryGetAttribute<ScopedMiddlewareAttribute>(out var att))
        {
            if (att.Scoping == MiddlewareScoping.Anywhere) return true;

            return att.Scoping == chain.Scoping;
        }

        // All good if no attribute
        return true;
    }
}

public static class ChainMiddlewareExtensions
{
    /// <summary>
    ///     Add a middleware method call to this chain's middleware pipeline
    /// </summary>
    /// <param name="chain">The chain to add middleware to</param>
    /// <param name="method">Expression pointing to the middleware method</param>
    /// <typeparam name="T">The middleware class type</typeparam>
    public static void AddMiddleware<T>(this IChain chain, Expression<Action<T>> method)
    {
        chain.Middleware.Add(new MethodCall(typeof(T), ReflectionHelper.GetMethod(method)!));
    }

    /// <summary>
    ///     Add a middleware method call to this chain's middleware pipeline
    /// </summary>
    /// <param name="chain">The chain to add middleware to</param>
    /// <param name="middlewareType">The middleware class type</param>
    /// <param name="methodName">The name of the method to call</param>
    [RequiresUnreferencedCode(
        "MethodCall reflects over middlewareType.GetMethod(methodName); the named method must survive trimming. " +
        "AOT-publishing apps should use the strongly-typed AddMiddleware<T>(Expression) overload or pre-generate " +
        "handlers via TypeLoadMode.Static.")]
    public static void AddMiddleware(this IChain chain,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type middlewareType, string methodName)
    {
        chain.Middleware.Add(new MethodCall(middlewareType, methodName));
    }

    /// <summary>
    ///     Add a postprocessor method call to this chain's postprocessor pipeline
    /// </summary>
    /// <param name="chain">The chain to add the postprocessor to</param>
    /// <param name="method">Expression pointing to the postprocessor method</param>
    /// <typeparam name="T">The middleware class type</typeparam>
    public static void AddPostprocessor<T>(this IChain chain, Expression<Action<T>> method)
    {
        chain.Postprocessors.Add(new MethodCall(typeof(T), ReflectionHelper.GetMethod(method)!));
    }

    /// <summary>
    ///     Add a postprocessor method call to this chain's postprocessor pipeline
    /// </summary>
    /// <param name="chain">The chain to add the postprocessor to</param>
    /// <param name="middlewareType">The middleware class type</param>
    /// <param name="methodName">The name of the method to call</param>
    [RequiresUnreferencedCode(
        "MethodCall reflects over middlewareType.GetMethod(methodName); the named method must survive trimming. " +
        "AOT-publishing apps should use the strongly-typed AddPostprocessor<T>(Expression) overload or pre-generate " +
        "handlers via TypeLoadMode.Static.")]
    public static void AddPostprocessor(this IChain chain,
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicMethods | DynamicallyAccessedMemberTypes.NonPublicMethods)]
        Type middlewareType, string methodName)
    {
        chain.Postprocessors.Add(new MethodCall(middlewareType, methodName));
    }
}

#region sample_ichain
/// <summary>
///     Models the middleware arrangement for either an HTTP route execution
///     or the execution of a message
/// </summary>
public interface IChain
{
    MiddlewareScoping Scoping { get; }
    
    void ApplyParameterMatching(MethodCall call);
    
    IdempotencyStyle Idempotency { get; set; }

    /// <summary>
    ///     GH-4180. When set, this chain enforces <b>logical</b> message deduplication: it resolves an
    ///     application-supplied id and refuses to run a second time for the same id. Null — the default —
    ///     means no logical deduplication, which is distinct from <see cref="Idempotency" />, whose
    ///     subject is <see cref="Envelope.Id" /> and therefore one delivery rather than one intent.
    /// </summary>
    /// <remarks>
    ///     Declared with a default implementation so that <see cref="IChain" /> implementors outside
    ///     Wolverine's own <see cref="Chain{TChain,TModifyAttribute}" /> hierarchy keep compiling. Every
    ///     chain type Wolverine ships overrides it with a real auto-property.
    /// </remarks>
    DeduplicationRequirement? Deduplication
    {
        get => null;
        set { }
    }

    /// <summary>
    ///     GH-4180. Does this chain need deduplication frames woven into it? Mirrors
    ///     <see cref="RequiresOutbox" /> — a predicate the code generation asks rather than a flag policies
    ///     have to keep in sync.
    /// </summary>
    bool RequiresDeduplication() => Deduplication is not null;

    /// <summary>
    ///     GH-4180. Build the frames that abort execution when the deduplication check fails.
    ///
    ///     <para>
    ///     This is the ONLY part of logical deduplication that differs between chain types. Resolving the
    ///     id and claiming it are identical everywhere and live in shared frames; what a caller is told
    ///     about the refusal is not — a message handler discards and acks, an HTTP endpoint owes the
    ///     caller a status code, and a gRPC method owes it a <c>StatusCode</c>. Splitting it here follows
    ///     the same seam as <see cref="AddStopConditionIfNull(Variable)" /> and
    ///     <see cref="CreateSimpleValidationFrame" />, so it composes with the existing middleware
    ///     ordering and <see cref="TryCatchFinallyFrame" /> handling instead of introducing new rules.
    ///     </para>
    /// </summary>
    /// <param name="condition">
    ///     A <c>bool</c> variable that is <see langword="true" /> when execution must stop.
    /// </param>
    /// <param name="outcome">Which of the two failures this is — the two are not interchangeable.</param>
    /// <param name="requirement">The requirement being enforced, for messages and metadata.</param>
    Frame[] BuildDeduplicationStopCondition(Variable condition, DeduplicationOutcome outcome,
        DeduplicationRequirement requirement)
        => throw new NotSupportedException(
            $"{GetType().FullNameInCode()} does not support logical message deduplication (GH-4180)");

    /// <summary>
    ///     GH-4180. Find the variable holding this chain's logical deduplication id.
    ///
    ///     <para>
    ///     The default handles every source expressible through <see cref="TryFindVariable" /> — a request
    ///     header, a member of the input type, a route value — and falls back to the conventional
    ///     <see cref="DeduplicationRequirement.DefaultHeaderName" /> header, which is right for HTTP and
    ///     gRPC. Message handlers override it, because their natural id lives on
    ///     <see cref="Envelope.DeduplicationId" />: that value is a first-class envelope property which
    ///     <c>EnvelopeSerializer</c> lifts off the wire into the property rather than leaving in
    ///     <see cref="Envelope.Headers" />, so a header lookup would never find it.
    ///     </para>
    /// </summary>
    Variable ResolveDeduplicationId(DeduplicationRequirement requirement)
    {
        var source = requirement.Source == ValueSource.Anything ? ValueSource.Header : requirement.Source;
        var key = requirement.Key ?? DeduplicationRequirement.DefaultHeaderName;

        if (TryFindVariable(key, source, typeof(string), out var variable))
        {
            return variable;
        }

        throw new InvalidOperationException(
            $"Cannot resolve a logical deduplication id for {Description}. No {source} value named '{key}' could be found. See GH-4180");
    }

    /// <summary>
    ///     Frames that would be initially placed in front of
    ///     the primary action(s)
    /// </summary>
    List<Frame> Middleware { get; }

    /// <summary>
    ///     Frames that would be initially placed behind the primary
    ///     action(s)
    /// </summary>
    List<Frame> Postprocessors { get; }

    /// <summary>
    ///     GH-3975. Frames that run after everything in <see cref="Postprocessors" /> — which is to say
    ///     after the transactional commit, because the commit is itself a postprocessor added by the
    ///     persistence provider's <c>ApplyTransactionSupport</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is a SEPARATE list rather than a position within <see cref="Postprocessors" /> on purpose.
    ///         "After the commit" cannot be expressed positionally without depending on the order the policies
    ///         happened to run in — the commit frame may not exist yet when a middleware policy appends, and an
    ///         application that gets the position right today gets it wrong the moment policy ordering changes,
    ///         silently and with every test still green. Concatenating a distinct list at frame-assembly time
    ///         makes the guarantee structural instead.
    ///     </para>
    ///     <para>
    ///         Frames here are NOT wrapped in a try/finally, so a commit that throws unwinds past them and they
    ///         do not run — which is the entire point of asking for "after the commit".
    ///     </para>
    /// </remarks>
    List<Frame> PostCommitPostprocessors { get; }

    /// <summary>
    ///     A description of this frame
    /// </summary>
    string Description { get; }

    List<AuditedMember> AuditedMembers { get; }
    Dictionary<string, object> Tags { get; }

    /// <summary>
    /// When set, indicates that this handler chain targets an ancillary message store
    /// identified by this marker type (e.g., IAncillaryStore). This is used to route
    /// incoming durable inbox envelopes to the correct store for transactional atomicity.
    /// </summary>
    Type? AncillaryStoreType { get; set; }

    /// <summary>
    /// <see langword="true"/> when this chain's compiled code resolves at least one
    /// dependency via service location rather than constructor / parameter injection.
    /// Recorded at codegen time. When set, the generated code creates a child scope that
    /// Wolverine primes so service-located <see cref="IMessageContext"/> / <see cref="IMessageBus"/>
    /// resolve to the same context the handler received rather than a duplicate. See GH-3001.
    /// </summary>
    bool UsesServiceLocation { get; }

    /// <summary>
    ///     Strategy for dealing with any return values from the handler methods
    /// </summary>
    IReturnVariableActionSource ReturnVariableActionSource { get; set; }

    /// <summary>
    /// Does this chain have any transactional middleware attached to it?
    /// </summary>
    bool IsTransactional { get; set; }

    /// <summary>
    ///     Used internally by Wolverine for "outbox" mechanics
    /// </summary>
    /// <returns></returns>
    bool ShouldFlushOutgoingMessages();

    bool RequiresOutbox();

    MethodCall[] HandlerCalls();

    /// <summary>
    ///     Find all of the service dependencies of the current chain
    /// </summary>
    /// <param name="container"></param>
    /// <param name="stopAtTypes"></param>
    /// <param name="chain"></param>
    /// <returns></returns>
    IEnumerable<Type> ServiceDependencies(IServiceContainer container, IReadOnlyList<Type> stopAtTypes);

    /// <summary>
    ///     Does this chain have the designated attribute type anywhere in
    ///     its handlers?
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    bool HasAttribute<T>() where T : Attribute;

    /// <summary>
    ///     The input type for this chain
    /// </summary>
    /// <returns></returns>
    Type? InputType();

    /// <summary>
    ///     Add a member of the message type to be audited during execution
    /// </summary>
    /// <param name="member"></param>
    /// <param name="heading"></param>
    void Audit(MemberInfo member, string? heading = null);

    /// <summary>
    ///     Help out the code generation a little bit by telling this chain
    ///     about a service dependency that will be used. Helps connect
    ///     transactional middleware
    /// </summary>
    /// <param name="type"></param>
    public void AddDependencyType(Type type);

    void ApplyImpliedMiddlewareFromHandlers(GenerationRules generationRules);
    
    /// <summary>
    /// Special usage to make the single result of this method call be the actual response type
    /// for the chain. For HTTP, this becomes the resource type written to the response. For message handlers,
    /// this could be part of InvokeAsync<T>() or just a cascading message
    /// </summary>
    /// <param name="methodCall"></param>
    void UseForResponse(MethodCall methodCall);

    /// <summary>
    ///     Find all variables returned by any handler call in this chain
    ///     that can be cast to T
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <returns></returns>
    IEnumerable<Variable> ReturnVariablesOfType<T>();

    /// <summary>
    ///     Find all variables returned by any handler call in this chain
    ///     that can be cast to the supplied type
    /// </summary>
    /// <returns></returns>
    IEnumerable<Variable> ReturnVariablesOfType(Type interfaceType);

    /// <summary>
    /// Used by code generation to find a simple value on input types, headers, route values,
    /// query string, or claims for use in loading other data
    /// </summary>
    /// <param name="valueName"></param>
    /// <param name="source"></param>
    /// <param name="valueType"></param>
    /// <param name="variable"></param>
    /// <returns></returns>
    bool TryFindVariable(string valueName, ValueSource source, Type valueType, out Variable variable);

    /// <summary>
    /// Used by code generation to add a middleware Frame that aborts the processing if the variable is null
    /// </summary>
    /// <param name="variable"></param>
    Frame[] AddStopConditionIfNull(Variable variable);

    /// <summary>
    /// Used by code generation to add a middleware Frame that aborts the processing if the variable is null
    /// </summary>
    /// <param name="variable"></param>
    Frame[] AddStopConditionIfNull(Variable data, Variable? identity, IDataRequirement requirement);

    /// <summary>
    /// Is the data described by this requirement required for execution to continue? This is normally just
    /// <see cref="IDataRequirement.Required"/>, but a chain type is allowed to force the data to be required.
    /// Wolverine's HTTP chains do exactly that for <see cref="OnMissing.EmptyContentWith204"/> on GET or QUERY
    /// endpoints, where returning an empty 204 is a benign outcome.
    /// </summary>
    bool IsDataRequired(IDataRequirement requirement) => requirement.Required;

    bool TryInferMessageIdentity(out PropertyInfo? property);

    /// <summary>
    /// Get the existing TryCatchFinallyFrame or create a new one for wrapping
    /// exception handling around the handler execution
    /// </summary>
    TryCatchFinallyFrame GetOrCreateTryCatchFinallyFrame();

    /// <summary>
    /// Create a Frame for simple validation based on a variable that contains
    /// string validation messages (IEnumerable&lt;string&gt;, string[], etc.)
    /// </summary>
    /// <param name="variable">The variable containing validation messages</param>
    /// <returns>A frame that checks for validation messages and aborts if any exist, or null if not supported</returns>
    Frame? CreateSimpleValidationFrame(Variable variable);

    /// <summary>
    /// Create a Frame for validation based on a RequirementResult variable.
    /// If Branch == Continue, processing continues. If Branch == Stop, processing aborts.
    /// </summary>
    /// <param name="variable">The variable containing the RequirementResult</param>
    /// <returns>A frame that checks the RequirementResult and aborts if Branch == Stop, or null if not supported</returns>
    Frame? CreateRequirementResultFrame(Variable variable);
}

#endregion