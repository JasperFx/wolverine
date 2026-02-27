using System.Threading;
using System.Threading.Tasks;

namespace Wolverine.Shims.MediatR;

/// <summary>
/// Handler for requests that return a response.
/// This is a shim interface compatible with MediatR's IRequestHandler&lt;TRequest, TResponse&gt;.
/// Handlers implementing this interface will be automatically discovered by Wolverine.
/// </summary>
/// <typeparam name="TRequest">The request type</typeparam>
/// <typeparam name="TResponse">The response type</typeparam>
public interface IRequestHandler<in TRequest, TResponse> where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles a request and returns a response
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Response from the request</returns>
    Task<TResponse> Handle(TRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Handler for requests that do not return a response.
/// This is a shim interface compatible with MediatR's IRequestHandler&lt;TRequest&gt;.
/// Handlers implementing this interface will be automatically discovered by Wolverine.
/// </summary>
/// <typeparam name="TRequest">The request type</typeparam>
public interface IRequestHandler<in TRequest> where TRequest : IRequest
{
    /// <summary>
    /// Handles a request without returning a response
    /// </summary>
    /// <param name="request">The request</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>A task representing the completion of the request</returns>
    Task Handle(TRequest request, CancellationToken cancellationToken);
}
