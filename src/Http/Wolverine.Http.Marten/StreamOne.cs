using System.Reflection;
using Marten.AspNetCore;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Wolverine.Http.Marten;

/// <summary>
/// HTTP endpoint return value that streams a single document as JSON directly
/// from Marten to the <see cref="HttpContext.Response"/>. Uses Marten.AspNetCore's
/// <see cref="QueryableExtensions.WriteSingle{T}"/> under the hood — the JSON is
/// written straight to the response stream without a deserialize/serialize round-trip.
/// <para>
/// Returns HTTP <c>404</c> if the query produces no result, <see cref="OnFoundStatus"/>
/// (default 200) if it does. The response also carries <see cref="HttpResponse.ContentLength"/>
/// and <see cref="HttpResponse.ContentType"/> set correctly.
/// </para>
/// <para>
/// <b>StreamOne vs StreamAggregate.</b> Use <see cref="StreamOne{T}"/> for regular
/// Marten documents — plain objects persisted via <c>session.Store()</c> and queried
/// with <c>session.Query&lt;T&gt;()</c>. Use <see cref="StreamAggregate{T}"/> when
/// the target is an event-sourced aggregate projected live from the event stream
/// (the "latest" aggregate snapshot).
/// </para>
/// </summary>
/// <typeparam name="T">The document type to stream.</typeparam>
public sealed class StreamOne<T> : IResult, IEndpointMetadataProvider
{
    private readonly IQueryable<T> _queryable;

    /// <summary>
    /// Create a <see cref="StreamOne{T}"/> wrapping a Marten <see cref="IQueryable{T}"/>.
    /// The query's first matching document is streamed as JSON; 404 if none.
    /// </summary>
    public StreamOne(IQueryable<T> queryable)
    {
        _queryable = queryable ?? throw new ArgumentNullException(nameof(queryable));
    }

    /// <summary>
    /// Status code written when the query produces a result. Defaults to 200.
    /// Use 201 (Created) on a POST that returns a freshly-created document, etc.
    /// </summary>
    public int OnFoundStatus { get; init; } = StatusCodes.Status200OK;

    /// <summary>
    /// Response content type. Defaults to <c>application/json</c>.
    /// </summary>
    public string ContentType { get; init; } = "application/json";

    /// <inheritdoc />
    public Task ExecuteAsync(HttpContext httpContext)
    {
        if (httpContext == null) throw new ArgumentNullException(nameof(httpContext));
        return _queryable.WriteSingle(httpContext, ContentType, OnFoundStatus);
    }

    /// <summary>
    /// Populates endpoint metadata so OpenAPI correctly advertises a
    /// <c>200: T</c> and <c>404</c> response for this endpoint.
    /// </summary>
    public static void PopulateMetadata(MethodInfo method, EndpointBuilder builder)
    {
        if (builder == null) throw new ArgumentNullException(nameof(builder));

        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status200OK, typeof(T), ["application/json"]));
        builder.Metadata.Add(new ProducesResponseTypeMetadata(
            StatusCodes.Status404NotFound, typeof(void), []));
    }
}
