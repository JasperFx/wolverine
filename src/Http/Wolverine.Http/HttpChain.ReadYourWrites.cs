using System.ComponentModel;
using JasperFx.Events.Daemon;
using Microsoft.AspNetCore.Http;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;

namespace Wolverine.Http;

// GH-5401. [WaitForProjection<T>] on an HTTP endpoint: the token travels in the request and response headers.
public partial class HttpChain
{
    /// <summary>
    ///     Moves the after-commit wait ahead of the response, where it can still write headers, and reads an incoming
    ///     token from the request instead of an envelope. Runs before the resource writers are added.
    /// </summary>
    private void applyReadYourWrites()
    {
        var afterCommit = PostCommitPostprocessors.OfType<WaitForAppendedEventsFrame>().FirstOrDefault();
        if (afterCommit != null)
        {
            PostCommitPostprocessors.Remove(afterCommit);

            // At the end of the postprocessors so far, which puts it after the commit and before the response
            Postprocessors.Add(afterCommit);
            afterCommit.ResultWriter = (typeof(HttpContext), typeof(ConsistencyTokenHeaders),
                nameof(ConsistencyTokenHeaders.Write));
        }

        foreach (var beforeRunning in Middleware.OfType<WaitForConsistencyTokenFrame>())
        {
            beforeRunning.TokenSource = (typeof(HttpContext), typeof(ConsistencyTokenHeaders),
                nameof(ConsistencyTokenHeaders.WaitForRequestTokenAsync));
        }
    }
}

/// <summary>
///     GH-5401. Reads and writes the <see cref="ReadYourWrites.ConsistencyTokenHeader" /> and
///     <see cref="ReadYourWrites.ProjectionTimeoutHeader" /> for endpoints with <c>[WaitForProjection&lt;T&gt;]</c>.
///     Called by generated code.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ConsistencyTokenHeaders
{
    public static void Write(HttpContext context, ProjectionWaitResult? result)
    {
        if (result == null) return;

        context.Response.Headers[ReadYourWrites.ConsistencyTokenHeader] = result.Position.ToString();
        if (!result.Reached)
        {
            context.Response.Headers[ReadYourWrites.ProjectionTimeoutHeader] = "true";
        }
    }

    public static async Task WaitForRequestTokenAsync(HttpContext httpContext, object store, Type[] projections,
        int timeoutMs, MessageContext context, CancellationToken token)
    {
        if (!httpContext.Request.Headers.TryGetValue(ReadYourWrites.ConsistencyTokenHeader, out var values)) return;

        var result = await ReadYourWrites
            .WaitForTokenAsync(store, values.ToString(), projections, timeoutMs, context, token)
            .ConfigureAwait(false);

        if (result is { Reached: false })
        {
            httpContext.Response.Headers[ReadYourWrites.ProjectionTimeoutHeader] = "true";
        }
    }
}
