using Wolverine.Http;

namespace WolverineWebApi;

#region sample_hybridhandler_with_null_httpcontext
public record DoHybrid(string Message);

public static class HybridHandler
{
    [WolverinePost("/hybrid")]
    public static async Task HandleAsync(DoHybrid command, HttpContext? context)
    {
        // What this, because it will be null if this is used within 
        // a message handler!
        if (context != null)
        {
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync(command.Message);
        }
    }
}

#endregion