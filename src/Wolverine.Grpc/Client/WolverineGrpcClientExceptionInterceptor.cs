using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.Extensions.Options;

namespace Wolverine.Grpc.Client;

/// <summary>
///     Client-side gRPC <see cref="Interceptor"/> that translates an incoming
///     <see cref="RpcException"/> into a typed .NET exception using the shared
///     <see cref="WolverineGrpcExceptionMapper.MapToException"/> table — the inverse direction of
///     <see cref="WolverineGrpcExceptionInterceptor"/>.
/// </summary>
/// <remarks>
///     <para>
///         Registered automatically by
///         <see cref="WolverineGrpcClientExtensions.AddWolverineGrpcClient{T}(Microsoft.Extensions.DependencyInjection.IServiceCollection,System.Action{WolverineGrpcClientOptions}?)"/>.
///         A per-client <see cref="WolverineGrpcClientOptions.MapRpcException"/> delegate, when set,
///         is consulted before the default table so callers can express service-specific mappings
///         without rewriting the table wholesale. Returning <c>null</c> from the delegate falls
///         through to the default mapping.
///     </para>
///     <para>
///         <b>Ordering contract:</b> this interceptor is registered <em>first</em> on the client so it
///         sits outermost in the call chain — if Polly / <c>AddStandardResilienceHandler()</c> is
///         composed on the same typed client, its retry loop fires <em>inside</em> this catch.
///         Translating <see cref="RpcException"/> to a typed exception before the retry loop would
///         defeat retry; keep this invariant when modifying the registration order in
///         <see cref="WolverineGrpcClientExtensions"/>.
///     </para>
///     <para>
///         Streaming is handled specifically: an <see cref="RpcException"/> in server-streaming,
///         client-streaming or duplex calls is thrown from the stream reader's <c>MoveNext</c>, not
///         from the outer call. The interceptor therefore wraps each stream reader with a
///         <see cref="MappingStreamReader{T}"/> that performs the translation per-<c>MoveNext</c>.
///     </para>
/// </remarks>
public sealed class WolverineGrpcClientExceptionInterceptor : Interceptor
{
    private readonly IOptionsMonitor<WolverineGrpcClientOptions> _options;
    private readonly string _name;

    public WolverineGrpcClientExceptionInterceptor(
        IOptionsMonitor<WolverineGrpcClientOptions> options,
        string name)
    {
        _options = options;
        _name = name;
    }

    public override TResponse BlockingUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        BlockingUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        try
        {
            return continuation(request, context);
        }
        catch (RpcException ex)
        {
            throw Translate(ex);
        }
    }

    public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncUnaryCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(request, context);

        var mapped = TranslateAsync(call.ResponseAsync);
        return new AsyncUnaryCall<TResponse>(
            mapped,
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
        TRequest request,
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncServerStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(request, context);
        return new AsyncServerStreamingCall<TResponse>(
            new MappingStreamReader<TResponse>(call.ResponseStream, this),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncClientStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(context);
        var mapped = TranslateAsync(call.ResponseAsync);
        return new AsyncClientStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            mapped,
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
        ClientInterceptorContext<TRequest, TResponse> context,
        AsyncDuplexStreamingCallContinuation<TRequest, TResponse> continuation)
    {
        var call = continuation(context);
        return new AsyncDuplexStreamingCall<TRequest, TResponse>(
            call.RequestStream,
            new MappingStreamReader<TResponse>(call.ResponseStream, this),
            call.ResponseHeadersAsync,
            call.GetStatus,
            call.GetTrailers,
            call.Dispose);
    }

    internal Exception Translate(RpcException exception)
    {
        var userOverride = _options.Get(_name).MapRpcException;
        if (userOverride != null)
        {
            var mapped = userOverride(exception);
            if (mapped != null)
            {
                return mapped;
            }
        }

        return WolverineGrpcExceptionMapper.MapToException(exception);
    }

    private async Task<TResponse> TranslateAsync<TResponse>(Task<TResponse> inner)
    {
        try
        {
            return await inner.ConfigureAwait(false);
        }
        catch (RpcException ex)
        {
            throw Translate(ex);
        }
    }

    private sealed class MappingStreamReader<T> : IAsyncStreamReader<T>
    {
        private readonly IAsyncStreamReader<T> _inner;
        private readonly WolverineGrpcClientExceptionInterceptor _owner;

        public MappingStreamReader(IAsyncStreamReader<T> inner, WolverineGrpcClientExceptionInterceptor owner)
        {
            _inner = inner;
            _owner = owner;
        }

        public T Current => _inner.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.MoveNext(cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException ex)
            {
                throw _owner.Translate(ex);
            }
        }
    }
}
