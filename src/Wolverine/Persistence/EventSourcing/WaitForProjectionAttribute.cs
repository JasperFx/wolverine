using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.Persistence.DocumentStores;
using Wolverine.Persistence.Requirements;
using Wolverine.Runtime;

namespace Wolverine.Persistence.EventSourcing;

/// <summary>
///     GH-5401. Read-your-writes for an asynchronous projection. After the handler or HTTP endpoint commits, wait until
///     <typeparamref name="T" /> has processed the events it appended, so the next read of that projection sees them.
///     An HTTP endpoint also returns the position of those events in the <c>Wolverine-Consistency-Token</c> header.
///     Before the handler or endpoint runs, a <c>Wolverine-Consistency-Token</c> on the request or incoming message is
///     waited for the same way, so a read endpoint can take the token from an earlier write.
/// </summary>
/// <remarks>
///     <para>
///         <typeparamref name="T" /> is the projection class or the document it writes. An inline or live projection
///         is already up to date when the commit returns, so there is nothing to wait for.
///     </para>
///     <para>
///         A wait that runs out never fails the request or the message: Wolverine logs a warning and carries on, and
///         an HTTP endpoint adds <c>Wolverine-Projection-Timeout: true</c> to the response.
///     </para>
///     <para>
///         Works with Marten, Polecat and Fisher, and with an ancillary store when the chain is routed to one. The
///         wait reads the store's progression table, so it works on any node.
///     </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class WaitForProjectionAttribute<T> : ModifyChainAttribute
{
    /// <summary>
    ///     How long to wait, in milliseconds. The default is 5 seconds.
    /// </summary>
    public int TimeoutMs { get; set; } = ProjectionWaitFrames.DefaultTimeoutMs;

    public override void Modify(IChain chain, GenerationRules rules, IServiceContainer container)
    {
        ProjectionWaitFrames.Apply(chain, rules, container, typeof(T), TimeoutMs);
    }
}

internal static class ProjectionWaitFrames
{
    public const int DefaultTimeoutMs = 5000;

    public static void Apply(IChain chain, GenerationRules rules, IServiceContainer container, Type projection,
        int timeoutMs)
    {
        var provider = DocumentStoreResolution.Resolve(chain, rules, container, projection, "[WaitForProjection]");
        var storeType = chain.DetermineAncillaryStoreType() is { } ancillary &&
                        provider.OwnsStorageType(ancillary, container)
            ? ancillary
            : provider.SessionTypes.DocumentStore;

        var afterCommit = chain.PostCommitPostprocessors.OfType<WaitForAppendedEventsFrame>().FirstOrDefault();
        if (afterCommit == null)
        {
            afterCommit = new WaitForAppendedEventsFrame(storeType, provider.SessionTypes.DocumentSession);

            // After every postprocessor, the commit included. An HTTP chain moves it ahead of the response.
            chain.PostCommitPostprocessors.Add(afterCommit);
        }

        afterCommit.Add(projection, timeoutMs);

        var beforeRunning = chain.Middleware.OfType<WaitForConsistencyTokenFrame>().FirstOrDefault();
        if (beforeRunning == null)
        {
            beforeRunning = new WaitForConsistencyTokenFrame(storeType);

            // Ahead of anything that loads data. An HTTP chain swaps in the request header as the token's source.
            chain.Middleware.Insert(0, beforeRunning);
        }

        beforeRunning.Add(projection, timeoutMs);
    }

    internal static string TypesArray(IEnumerable<Type> types)
    {
        return $"new System.Type[]{{{types.Select(x => $"typeof({x.FullNameInCode()})").Join(", ")}}}";
    }
}

/// <summary>
///     GH-5401. After the commit: wait for the projections to process what the chain's session appended.
/// </summary>
internal class WaitForAppendedEventsFrame : AsyncFrame
{
    private readonly Type _sessionType;
    private readonly Type _storeType;
    private Variable? _cancellation;
    private Variable? _context;
    private Variable? _session;
    private Variable? _store;
    private Variable? _resultTarget;

    public WaitForAppendedEventsFrame(Type storeType, Type sessionType)
    {
        _storeType = storeType;
        _sessionType = sessionType;
    }

    public List<Type> Projections { get; } = [];
    public int TimeoutMs { get; private set; }

    /// <summary>
    ///     Set by Wolverine.Http: a public static method taking (target, ProjectionWaitResult?) that is called after
    ///     the wait, and the type of the target variable (the HttpContext)
    /// </summary>
    public (Type TargetType, Type HandlerType, string MethodName)? ResultWriter { get; set; }

    public void Add(Type projection, int timeoutMs)
    {
        if (!Projections.Contains(projection)) Projections.Add(projection);
        TimeoutMs = Math.Max(TimeoutMs, timeoutMs);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        // Only a session the chain already has can have appended anything. Never open one just to ask.
        _session = chain.TryFindVariable(_sessionType, VariableSource.Existing);
        if (_session == null) yield break;

        yield return _session;

        _store = chain.FindVariable(_storeType);
        yield return _store;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (ResultWriter.HasValue)
        {
            _resultTarget = chain.FindVariable(ResultWriter.Value.TargetType);
            yield return _resultTarget;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        if (_session != null)
        {
            writer.BlankLine();
            writer.WriteComment(
                $"[WaitForProjection]: wait for {Projections.Select(x => x.Name).Join(", ")} to process the events this session appended");
            var call =
                $"await {typeof(ReadYourWrites).FullNameInCode()}.{nameof(ReadYourWrites.WaitForAppendedEventsAsync)}({_store!.Usage}, {_session.Usage}, {ProjectionWaitFrames.TypesArray(Projections)}, {TimeoutMs}, {_context!.Usage}, {_cancellation!.Usage}).ConfigureAwait(false)";

            if (ResultWriter is { } resultWriter)
            {
                writer.Write($"var projectionWait = {call};");
                writer.Write(
                    $"{resultWriter.HandlerType.FullNameInCode()}.{resultWriter.MethodName}({_resultTarget!.Usage}, projectionWait);");
            }
            else
            {
                writer.Write($"{call};");
            }
        }

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
///     GH-5401. Before the handler runs: wait for the position in an incoming <c>Wolverine-Consistency-Token</c>.
/// </summary>
internal class WaitForConsistencyTokenFrame : AsyncFrame
{
    private readonly Type _storeType;
    private Variable? _cancellation;
    private Variable? _context;
    private Variable? _store;
    private Variable? _tokenSource;

    public WaitForConsistencyTokenFrame(Type storeType)
    {
        _storeType = storeType;
    }

    public List<Type> Projections { get; } = [];
    public int TimeoutMs { get; private set; }

    /// <summary>
    ///     Set by Wolverine.Http: a public static method taking (target, store, Type[], int, MessageContext,
    ///     CancellationToken) that reads the token from the target (the HttpContext) and waits. Without it, the
    ///     token comes from the incoming envelope.
    /// </summary>
    public (Type TargetType, Type HandlerType, string MethodName)? TokenSource { get; set; }

    public void Add(Type projection, int timeoutMs)
    {
        if (!Projections.Contains(projection)) Projections.Add(projection);
        TimeoutMs = Math.Max(TimeoutMs, timeoutMs);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        _store = chain.FindVariable(_storeType);
        yield return _store;

        _context = chain.FindVariable(typeof(MessageContext));
        yield return _context;

        _cancellation = chain.FindVariable(typeof(CancellationToken));
        yield return _cancellation;

        if (TokenSource.HasValue)
        {
            _tokenSource = chain.FindVariable(TokenSource.Value.TargetType);
            yield return _tokenSource;
        }
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment(
            $"[WaitForProjection]: wait for {Projections.Select(x => x.Name).Join(", ")} to reach an incoming {ReadYourWrites.ConsistencyTokenHeader}");

        var types = ProjectionWaitFrames.TypesArray(Projections);
        if (TokenSource is { } source)
        {
            writer.Write(
                $"await {source.HandlerType.FullNameInCode()}.{source.MethodName}({_tokenSource!.Usage}, {_store!.Usage}, {types}, {TimeoutMs}, {_context!.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        }
        else
        {
            writer.Write(
                $"await {typeof(ReadYourWrites).FullNameInCode()}.{nameof(ReadYourWrites.WaitForEnvelopeTokenAsync)}({_store!.Usage}, {types}, {TimeoutMs}, {_context!.Usage}, {_cancellation!.Usage}).ConfigureAwait(false);");
        }

        Next?.GenerateCode(method, writer);
    }
}
