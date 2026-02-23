using System.Threading;
using System.Threading.Tasks;

namespace Wolverine.Shims.MediatR;

/// <summary>
/// Marker interface for requests that return a response.
/// This is a shim interface compatible with MediatR's IRequest&lt;T&gt;.
/// </summary>
/// <typeparam name="T">The response type</typeparam>
public interface IRequest<out T>
{
}

/// <summary>
/// Marker interface for requests that do not return a response.
/// This is a shim interface compatible with MediatR's IRequest.
/// </summary>
public interface IRequest
{
}
