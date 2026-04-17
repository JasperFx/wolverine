using Wolverine.Http;

namespace WolverineWebApi.Streaming;

public static class StreamingEndpoints
{
    #region sample_sse_endpoint
    [WolverineGet("/api/sse/events")]
    public static IResult GetSseEvents()
    {
        return Results.Stream(async stream =>
        {
            var writer = new StreamWriter(stream);
            for (var i = 0; i < 3; i++)
            {
                await writer.WriteAsync($"data: Event {i}\n\n");
                await writer.FlushAsync();
            }
        }, contentType: "text/event-stream");
    }

    #endregion

    #region sample_streaming_endpoint
    [WolverineGet("/api/stream/data")]
    public static IResult GetStreamData()
    {
        return Results.Stream(async stream =>
        {
            var writer = new StreamWriter(stream);
            for (var i = 0; i < 5; i++)
            {
                await writer.WriteLineAsync($"line {i}");
                await writer.FlushAsync();
            }
        }, contentType: "text/plain");
    }

    #endregion
}
