using Google.Rpc;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Status = Google.Rpc.Status;

namespace Wolverine.Grpc;

/// <summary>
///     Server-side gRPC interceptor that translates .NET exceptions thrown inside Wolverine-backed
///     gRPC service methods into <see cref="RpcException"/>s. Registered automatically by
///     <see cref="WolverineGrpcExtensions.AddWolverineGrpc(Microsoft.Extensions.DependencyInjection.IServiceCollection)"/>.
/// </summary>
/// <remarks>
///     <para>
///         Two translation layers compose, in order:
///         <list type="number">
///             <item>
///                 Registered <see cref="IGrpcStatusDetailsProvider"/>s are consulted in DI
///                 order. The first non-null <see cref="Google.Rpc.Status"/> wins and is emitted
///                 via <see cref="Grpc.Core.StatusProtoExtensions.ToRpcException(Status)"/> —
///                 packing any detail payloads into the <c>grpc-status-details-bin</c> trailer.
///             </item>
///             <item>
///                 If every provider returns null (or none are registered), the default
///                 <see cref="WolverineGrpcExceptionMapper"/> table maps the exception to a bare
///                 <see cref="StatusCode"/> per AIP-193 §3.11. This preserves the pre-M11 behaviour
///                 when rich details are not opted in.
///             </item>
///         </list>
///     </para>
///     <para>
///         Applies to both code-first (protobuf-net.Grpc) and proto-first (Grpc.Tools) services,
///         since both route through the same ASP.NET Core gRPC pipeline. Unary and server-streaming
///         RPCs are intercepted; client-streaming and bidirectional are deferred pending the
///         matching <c>IMessageBus</c> overloads.
///     </para>
/// </remarks>
public sealed class WolverineGrpcExceptionInterceptor : Interceptor
{
    private readonly ILogger<WolverineGrpcExceptionInterceptor> _logger;
    private readonly WolverineGrpcOptions _options;

    public WolverineGrpcExceptionInterceptor(
        ILogger<WolverineGrpcExceptionInterceptor> logger,
        WolverineGrpcOptions options)
    {
        _logger = logger;
        _options = options;
    }

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw Translate(ex, context);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context);
        }
        catch (Exception ex) when (ex is not RpcException)
        {
            throw Translate(ex, context);
        }
    }

    private RpcException Translate(Exception exception, ServerCallContext context)
    {
        var richStatus = TryBuildRichStatus(exception, context);
        if (richStatus != null)
        {
            _logger.LogWarning(
                exception,
                "Mapped {ExceptionType} thrown by {Method} to rich gRPC status {Code} with {DetailCount} detail(s)",
                exception.GetType().FullName,
                context.Method,
                (Code)richStatus.Code,
                richStatus.Details.Count);
            return richStatus.ToRpcException();
        }

        var userCode = _options.TryMapException(exception);
        if (userCode.HasValue)
        {
            var rpc = new RpcException(new global::Grpc.Core.Status(userCode.Value, exception.Message));
            _logger.LogWarning(
                exception,
                "Mapped {ExceptionType} thrown by {Method} to gRPC status {StatusCode} (user-configured mapping)",
                exception.GetType().FullName,
                context.Method,
                userCode.Value);
            return rpc;
        }

        var mapped = WolverineGrpcExceptionMapper.ToRpcException(exception);
        _logger.LogWarning(
            exception,
            "Mapped {ExceptionType} thrown by {Method} to gRPC status {StatusCode}",
            exception.GetType().FullName,
            context.Method,
            mapped.StatusCode);
        return mapped;
    }

    private static Status? TryBuildRichStatus(Exception exception, ServerCallContext context)
    {
        var services = context.GetHttpContext().RequestServices;
        var providers = services.GetServices<IGrpcStatusDetailsProvider>();
        foreach (var provider in providers)
        {
            var status = provider.BuildStatus(exception, context);
            if (status != null) return status;
        }

        return null;
    }
}
