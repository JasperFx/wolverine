using JasperFx.CodeGeneration;
using JasperFx.CodeGeneration.Frames;
using JasperFx.CodeGeneration.Model;
using JasperFx.Core.Reflection;
using Wolverine.Persistence;

namespace Wolverine.Grpc.MultiTenancy;

/// <summary>
///     Code-generation frame that runs the configured tenant detection strategies at the top of a
///     generated gRPC service method. The gRPC counterpart to Wolverine.Http's
///     <c>DetectTenantIdFrame</c>. Emits:
///     <list type="number">
///         <item><c>var tenantId = await _grpcOptions.TryDetectTenantIdAsync(...)</c> — declaring
///             the <c>tenantId</c> variable (<see cref="PersistenceConstants.TenantIdVariableName"/>)
///             that Marten's <c>OpenMartenSessionFrame</c> and Polecat's
///             <c>OpenPolecatSessionFrame</c> already look for, so tenant-scoped sessions opened by
///             middleware frames in the same generated method pick the tenant up structurally,
///             without relying on the ambient <see cref="IMessageContext"/>.</item>
///         <item>Optionally an <c>AssertExists()</c> guard throwing
///             <c>RpcException(InvalidArgument)</c>.</item>
///         <item>Application of the detected tenant to the scoped bus — either directly on the
///             wrapper's injected <see cref="IMessageBus"/> field (proto-first and code-first
///             wrappers, whose subsequent <c>InvokeAsync</c>/<c>StreamAsync</c> then carries the
///             tenant onto the envelope) or via the request-scoped <see cref="IMessageContext"/>
///             (hand-written delegation wrappers, which have no bus field).</item>
///     </list>
///     One instance is created per generated RPC method by each chain's <c>AssembleTypes</c> —
///     frames hold per-method mutable state and cannot be shared across method bodies.
/// </summary>
internal sealed class DetectGrpcTenantIdFrame : AsyncFrame
{
    private readonly GrpcTenantIdDetection _detection;
    private readonly InjectedField _optionsField;
    private readonly string _serverCallContextExpression;
    private readonly InjectedField? _busField;
    private readonly InjectedField? _serviceProviderField;

    public DetectGrpcTenantIdFrame(GrpcTenantIdDetection detection, InjectedField optionsField,
        string serverCallContextExpression, InjectedField? busField, InjectedField? serviceProviderField)
    {
        _detection = detection;
        _optionsField = optionsField;
        _serverCallContextExpression = serverCallContextExpression;
        _busField = busField;
        _serviceProviderField = serviceProviderField;

        TenantId = new Variable(typeof(string), PersistenceConstants.TenantIdVariableName, this);
    }

    public Variable TenantId { get; }

    public override void GenerateCode(GeneratedMethod method, ISourceWriter writer)
    {
        writer.BlankLine();
        writer.WriteComment("Tenant Id detection");
        if (_detection.ZeroConfigDefaultApplied)
        {
            writer.WriteComment(
                "(zero-config default: no explicit TenantId configuration, detecting the 'tenant-id' metadata header stamped by the Wolverine gRPC client interceptor)");
        }

        for (var i = 0; i < _detection.Strategies.Count; i++)
        {
            writer.WriteComment($"{i + 1}. {_detection.Strategies[i]}");
        }

        writer.Write(
            $"var {TenantId.Usage} = await {_optionsField.Usage}.{nameof(WolverineGrpcOptions.TryDetectTenantIdAsync)}({_serverCallContextExpression});");

        if (_detection.AssertTenantExists)
        {
            writer.Write(
                $"{typeof(GrpcTenantDetection).FullNameInCode()}.{nameof(GrpcTenantDetection.AssertTenantIdExists)}({TenantId.Usage});");
        }

        if (_busField != null)
        {
            writer.Write($"BLOCK:if (!string.{nameof(string.IsNullOrEmpty)}({TenantId.Usage}))");
            writer.Write($"{_busField.Usage}.{nameof(IMessageBus.TenantId)} = {TenantId.Usage};");
            writer.FinishBlock();
        }
        else if (_serviceProviderField != null)
        {
            writer.Write(
                $"{typeof(GrpcTenantDetection).FullNameInCode()}.{nameof(GrpcTenantDetection.TryApplyToAmbientContext)}({_serviceProviderField.Usage}, {TenantId.Usage});");
        }

        writer.BlankLine();

        Next?.GenerateCode(method, writer);
    }
}
