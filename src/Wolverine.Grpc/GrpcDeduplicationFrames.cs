using Grpc.Core;
using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using System.Reflection;
using JasperFx.Core.Reflection;
using Wolverine.Configuration;
using Wolverine.Persistence;
using Wolverine.Persistence.Codegen;
using Wolverine.Attributes;

namespace Wolverine.Grpc;

/// <summary>
/// GH-4180. The gRPC answer to a failed logical deduplication check: an <see cref="RpcException" />
/// carrying the canonical status code for the condition.
///
/// <para>
/// A duplicate is <see cref="StatusCode.AlreadyExists" /> and a missing-but-required key is
/// <see cref="StatusCode.InvalidArgument" />, both straight out of
/// <see href="https://google.aip.dev/193">AIP-193</see> — the same table
/// <c>WolverineGrpcExceptionInterceptor</c> already maps ordinary .NET exceptions through, so a gRPC
/// client sees deduplication refusals in exactly the shape it already handles every other refusal in.
/// </para>
///
/// <para>
/// Throwing rather than returning is not a stylistic choice: a unary RPC method has to produce a
/// response message, and there is no honest response body for "this was already done". The status is
/// the answer.
/// </para>
/// </summary>
internal class GrpcDeduplicationStopFrame : SyncFrame
{
    private readonly Variable _condition;
    private readonly StatusCode _statusCode;
    private readonly string _message;

    public GrpcDeduplicationStopFrame(Variable condition, StatusCode statusCode, string message)
    {
        _condition = condition;
        _statusCode = statusCode;
        _message = message.Replace("\"", "'");
        uses.Add(condition);
    }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        var statusCode = typeof(StatusCode).FullNameInCode();
        var status = typeof(Status).FullNameInCode();
        var rpcException = typeof(RpcException).FullNameInCode();

        writer.WriteComment($"GH-4180: {_statusCode} for a failed logical deduplication check");
        writer.Write($"BLOCK:if ({_condition.Usage})");
        writer.Write(
            $"throw new {rpcException}(new {status}({statusCode}.{_statusCode}, \"{_message}\"));");
        writer.FinishBlock();

        Next?.GenerateCode(method, writer);
    }
}

/// <summary>
/// GH-4180. Reads the logical deduplication id out of the incoming call's request metadata.
///
/// <para>
/// gRPC lower-cases every metadata key on the wire, so the configured header name is lower-cased
/// here too. A caller sending <c>Idempotency-Key</c> and a chain configured for
/// <c>Idempotency-Key</c> would otherwise never match, and the failure would look like "the client
/// is not sending a key" rather than a casing bug.
/// </para>
///
/// <para>
/// Takes the <see cref="ServerCallContext" /> as an expression string rather than a
/// <see cref="Variable" />, matching <c>DetectGrpcTenantIdFrame</c>: gRPC generates each RPC method
/// from the proto/contract signature, so the context is a method parameter whose name differs per
/// method, and every chain flavour already threads that name through its own
/// <c>AssembleTypes</c>.
/// </para>
/// </summary>
internal class ReadGrpcDeduplicationIdFrame : SyncFrame
{
    private readonly string _serverCallContextExpression;
    private readonly string _headerName;

    public ReadGrpcDeduplicationIdFrame(string serverCallContextExpression, string headerName)
    {
        _serverCallContextExpression = serverCallContextExpression;
        _headerName = headerName.ToLowerInvariant();
        Variable = new Variable(typeof(string), "deduplicationId", this);
    }

    public Variable Variable { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.WriteComment($"GH-4180: logical deduplication id from the '{_headerName}' request metadata");
        writer.Write(
            $"var {Variable.Usage} = {_serverCallContextExpression}.{nameof(ServerCallContext.RequestHeaders)}?.{nameof(Metadata.GetValue)}(\"{_headerName}\");");

        Next?.GenerateCode(method, writer);
    }

    public override IEnumerable<Variable> FindVariables(IMethodVariables chain)
    {
        yield break;
    }
}

/// <summary>
/// GH-4180. Shared implementation of <see cref="IChain.BuildDeduplicationStopCondition" /> for all
/// three gRPC chain flavours — proto-first, code-first, and hand-written. The refusal is identical
/// in each; only the surrounding codegen differs, so the three overrides delegate here rather than
/// repeating the mapping and drifting apart.
/// </summary>
internal static class GrpcDeduplication
{
    /// <summary>
    /// Resolve the deduplication requirement for ONE RPC method, or null when it is not deduplicated.
    ///
    /// <para>
    /// Read directly off the attributes rather than through <c>IChain.Deduplication</c>, because the
    /// gRPC chains never run <c>applyAttributesAndConfigureMethods</c> — the pass that applies
    /// <c>ModifyChainAttribute</c> for handler and HTTP chains. Wiring that pass in wholesale would
    /// apply every chain-modifying attribute to gRPC services at once, which is a much larger
    /// behaviour change than this feature is entitled to make.
    /// </para>
    ///
    /// <para>
    /// Resolution is per RPC method, not per chain, and the method attribute beats the service one.
    /// A gRPC chain is a whole SERVICE — one type with many RPCs — so a chain-level requirement would
    /// force "all of this service's calls are deduplicated or none are". Attributing one RPC is the
    /// natural thing to want, and the natural thing to write.
    /// </para>
    /// </summary>
    public static DeduplicationRequirement? RequirementFor(IChain chain, Type serviceType, MethodInfo rpcMethod)
    {
        var attribute = rpcMethod.GetCustomAttribute<DeduplicatedAttribute>()
                        ?? serviceType.GetCustomAttribute<DeduplicatedAttribute>();

        if (attribute == null) return chain.Deduplication;

        return new DeduplicationRequirement
        {
            Source = attribute.Source,
            Key = attribute.Key,
            Required = attribute.Required,
            DuplicateStatusCode = attribute.DuplicateStatusCode
        };
    }

    /// <summary>
    /// Does any RPC on this service need deduplication? Answers whether the generated type has to
    /// declare an <see cref="IMessageDeduplicator" /> field at all — a service that did not opt in
    /// takes on no extra dependency.
    /// </summary>
    public static bool AnyRequires(IChain chain, Type serviceType, IEnumerable<MethodInfo> rpcMethods)
        => rpcMethods.Any(m => RequirementFor(chain, serviceType, m) != null);

    /// <summary>
    /// Weave the deduplication frames into ONE generated RPC method.
    ///
    /// <para>
    /// Per method, and building fresh frames each time, rather than through the chain's
    /// <c>Middleware</c> list the way handler and HTTP chains do. Frames carry per-method mutable
    /// state (their resolved variables and their <c>Next</c> link), and gRPC's <c>CloneFrames</c>
    /// only deep-copies <c>ConstructorFrame</c> and <c>MethodCall</c> — everything else is passed
    /// through by reference. Sharing one instance across a service's RPC methods would corrupt all
    /// but the last. This is the same rule <c>DetectGrpcTenantIdFrame</c> documents.
    /// </para>
    /// </summary>
    /// <param name="deduplicatorField">
    /// The generated type's injected <see cref="IMessageDeduplicator" />. Passed explicitly because
    /// gRPC service types are composed from declared <c>InjectedField</c>s rather than the
    /// container-driven variable sources handler and HTTP chains resolve against.
    /// </param>
    public static IEnumerable<Frame> BuildFrames(IChain chain, DeduplicationRequirement requirement,
        string serverCallContextExpression, InjectedField deduplicatorField)
    {
        if (requirement.Source is not (ValueSource.Anything or ValueSource.Header))
        {
            throw new NotSupportedException(
                $"Wolverine gRPC services can only source a logical deduplication id from request metadata (ValueSource.Header), not {requirement.Source}. Requested by {chain.Description}. See GH-4180");
        }

        var headerName = requirement.Key ?? DeduplicationRequirement.DefaultHeaderName;
        var cancellation = $"{serverCallContextExpression}.{nameof(ServerCallContext.CancellationToken)}";

        var read = new ReadGrpcDeduplicationIdFrame(serverCallContextExpression, headerName);
        yield return read;

        if (requirement.Required)
        {
            var missing = new DeduplicationIdMissingFrame(read.Variable);
            yield return missing;

            foreach (var frame in chain.BuildDeduplicationStopCondition(missing.Variable,
                         DeduplicationOutcome.MissingId, requirement))
            {
                yield return frame;
            }
        }

        var claim = new ClaimDeduplicationIdFrame(read.Variable, chain.AncillaryStoreType,
            deduplicatorField.Usage, cancellation);
        yield return claim;

        foreach (var frame in chain.BuildDeduplicationStopCondition(claim.Variable,
                     DeduplicationOutcome.Duplicate, requirement))
        {
            yield return frame;
        }

        // A gRPC service method is never enrolled in a Wolverine transaction of its own -- it forwards to
        // the bus, and any transaction belongs to the handler on the other side. So the claim is always
        // already committed by the time the forward runs, and the compensating release is always needed.
        yield return new ReleaseDeduplicationIdOnFailureFrame(read.Variable, chain.AncillaryStoreType,
            deduplicatorField.Usage, cancellation);
    }

    public static Frame[] BuildStopCondition(Variable condition, DeduplicationOutcome outcome,
        DeduplicationRequirement requirement)
    {
        // Lower-cased to match what a caller must actually send: Grpc.Core.Metadata rejects a key with
        // upper-case characters outright, so naming the header as configured ("Idempotency-Key") in an
        // error message would send the reader off to write a call that cannot compile.
        var key = (requirement.Key ?? DeduplicationRequirement.DefaultHeaderName).ToLowerInvariant();

        return outcome switch
        {
            DeduplicationOutcome.Duplicate =>
            [
                new GrpcDeduplicationStopFrame(condition, StatusCode.AlreadyExists,
                    $"A call with this '{key}' has already been handled")
            ],
            DeduplicationOutcome.MissingId =>
            [
                new GrpcDeduplicationStopFrame(condition, StatusCode.InvalidArgument,
                    $"This method requires a logical deduplication id in the '{key}' request metadata")
            ],
            _ => throw new ArgumentOutOfRangeException(nameof(outcome))
        };
    }
}
