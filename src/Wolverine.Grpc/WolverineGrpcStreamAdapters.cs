using System.Runtime.CompilerServices;
using Grpc.Core;

namespace Wolverine.Grpc;

/// <summary>
///     Adapters used by Wolverine's generated gRPC wrappers to bridge gRPC streaming primitives
///     to the shapes <see cref="IMessageBus"/> works with.
/// </summary>
/// <remarks>
///     Grpc.Core.Api and Grpc.Net.Common both declare a <c>Grpc.Core.AsyncStreamReaderExtensions</c>
///     type, which makes any generated call to their <c>ReadAllAsync</c> ambiguous (CS0433) in an
///     application referencing both. Wolverine's own adapter sidesteps the collision.
/// </remarks>
public static class WolverineGrpcStreamAdapters
{
    /// <summary>
    ///     Exposes an <see cref="IAsyncStreamReader{T}"/> as an <see cref="IAsyncEnumerable{T}"/> so a
    ///     client-streaming RPC's inbound stream can be handed to
    ///     <see cref="IMessageBus.InvokeStreamAsync{TRequest, TResponse}(IAsyncEnumerable{TRequest}, CancellationToken, TimeSpan?)"/>.
    /// </summary>
    public static async IAsyncEnumerable<T> ReadAllAsync<T>(IAsyncStreamReader<T> reader,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (await reader.MoveNext(cancellationToken).ConfigureAwait(false))
        {
            yield return reader.Current;
        }
    }
}
