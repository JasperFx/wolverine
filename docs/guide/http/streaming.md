# Streaming and Server-Sent Events (SSE)

Wolverine.HTTP endpoints can return ASP.NET Core's `IResult` type, which means you can take full advantage of the built-in `Results.Stream()` method for streaming responses and Server-Sent Events (SSE). No special Wolverine infrastructure is needed -- this is standard ASP.NET Core functionality that works seamlessly with Wolverine's endpoint model.

## Server-Sent Events (SSE)

[Server-Sent Events](https://developer.mozilla.org/en-US/docs/Web/API/Server-sent_events) allow a server to push real-time updates to the client over a single HTTP connection. To create an SSE endpoint, return `Results.Stream()` with the `text/event-stream` content type:

<!-- snippet: sample_sse_endpoint -->
<a id='snippet-sample_sse_endpoint'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Streaming/StreamingEndpoints.cs#L8-L22' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_sse_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

SSE messages follow the [EventSource format](https://html.spec.whatwg.org/multipage/server-sent-events.html), where each event is prefixed with `data: ` and terminated by two newline characters (`\n\n`).

## Plain Streaming Responses

For streaming plain text or other content types without the SSE protocol, you can use the same `Results.Stream()` approach with a different content type:

<!-- snippet: sample_streaming_endpoint -->
<a id='snippet-sample_streaming_endpoint'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Streaming/StreamingEndpoints.cs#L24-L40' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_streaming_endpoint' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Integration with Wolverine Services

Since Wolverine HTTP endpoints support dependency injection, you can combine streaming with other Wolverine capabilities. For example, you could stream events from a Wolverine message handler or publish messages during a streaming session:

```cs
[WolverineGet("/api/sse/orders")]
public static IResult StreamOrderUpdates(IMessageBus bus)
{
    return Results.Stream(async stream =>
    {
        var writer = new StreamWriter(stream);

        // Your streaming logic here, potentially using
        // Wolverine's IMessageBus for publishing side effects
        await writer.WriteAsync("data: connected\n\n");
        await writer.FlushAsync();
    }, contentType: "text/event-stream");
}
```

## Key Points

- Wolverine endpoints that return `IResult` work with `Results.Stream()` out of the box
- No special Wolverine middleware or configuration is required for streaming
- SSE endpoints should use the `text/event-stream` content type
- Each SSE event must be formatted as `data: <payload>\n\n`
- Always call `FlushAsync()` after writing each event to ensure timely delivery to the client
- The `Results.Stream()` API is part of ASP.NET Core and is available in .NET 8+
