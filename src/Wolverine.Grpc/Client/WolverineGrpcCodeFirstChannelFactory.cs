using System.Collections.Concurrent;
using Grpc.Net.Client;
using Microsoft.Extensions.Options;

namespace Wolverine.Grpc.Client;

/// <summary>
///     Singleton factory that caches and disposes <see cref="GrpcChannel"/> instances for
///     code-first typed clients. Proto-first clients ride on <c>IHttpClientFactory</c> and do
///     not need this — protobuf-net.Grpc code-first clients do, because their DI seam is
///     <c>channel.CreateGrpcService&lt;T&gt;()</c> rather than a factory-managed
///     <see cref="HttpClient"/> pool.
/// </summary>
/// <remarks>
///     One channel per named client is kept alive for the lifetime of the service provider,
///     matching <c>AddGrpcClient&lt;T&gt;()</c>'s "one channel per registration" shape. The
///     factory is <see cref="IDisposable"/> so app shutdown cleanly tears down HTTP/2
///     connections without relying on GC finalisation.
/// </remarks>
public sealed class WolverineGrpcCodeFirstChannelFactory : IDisposable
{
    private readonly ConcurrentDictionary<string, GrpcChannel> _channels = new();
    private readonly IOptionsMonitor<WolverineGrpcCodeFirstClientOptions> _options;
    private int _disposed;

    public WolverineGrpcCodeFirstChannelFactory(IOptionsMonitor<WolverineGrpcCodeFirstClientOptions> options)
    {
        _options = options;
    }

    public GrpcChannel GetOrCreate(string name, Uri address)
    {
        if (_disposed != 0)
        {
            throw new ObjectDisposedException(nameof(WolverineGrpcCodeFirstChannelFactory));
        }

        return _channels.GetOrAdd(name, n =>
        {
            var channelOptions = new GrpcChannelOptions();
            var configs = _options.Get(n).ChannelConfigurations;
            foreach (var configure in configs)
            {
                configure(channelOptions);
            }

            return GrpcChannel.ForAddress(address, channelOptions);
        });
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var channel in _channels.Values)
        {
            channel.Dispose();
        }

        _channels.Clear();
    }
}
