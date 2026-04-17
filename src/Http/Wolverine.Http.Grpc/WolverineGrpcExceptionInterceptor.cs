using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Logging;

namespace Wolverine.Http.Grpc;

/// <summary>
///     Server-side gRPC interceptor that translates .NET exceptions thrown inside Wolverine-backed
///     gRPC service methods into <see cref="RpcException"/>s with the canonical <see cref="StatusCode"/>
///     from <see cref="WolverineGrpcExceptionMapper"/>. Registered automatically by
///     <see cref="WolverineGrpcExtensions.AddWolverineGrpc"/>.
/// </summary>
/// <remarks>
///     Applies to both code-first (protobuf-net.Grpc) and proto-first (Grpc.Tools) services, since
///     both route through the same ASP.NET Core gRPC pipeline. Unary and server-streaming RPCs are
///     intercepted today; client-streaming and bidirectional are deferred pending the matching
///     <c>IMessageBus</c> overloads.
/// </remarks>
public sealed class WolverineGrpcExceptionInterceptor : Interceptor
{
    private readonly ILogger<WolverineGrpcExceptionInterceptor> _logger;

    public WolverineGrpcExceptionInterceptor(ILogger<WolverineGrpcExceptionInterceptor> logger)
    {
        _logger = logger;
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
        var mapped = WolverineGrpcExceptionMapper.ToRpcException(exception);
        _logger.LogWarning(
            exception,
            "Mapped {ExceptionType} thrown by {Method} to gRPC status {StatusCode}",
            exception.GetType().FullName,
            context.Method,
            mapped.StatusCode);
        return mapped;
    }
}
