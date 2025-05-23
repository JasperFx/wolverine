# Using as Mediator

::: info
This isn't what Wolverine was originally designed to do, but seems to be a popular use case for teams
struggling with ASP.Net Core MVC Controller bloat.
:::

For one reason or another, many teams will use Wolverine strictly as a "mediator" that is used to simplify
MVC Controllers by offloading the actual request handling like so:

<!-- snippet: sample_using_as_mediator -->
<a id='snippet-sample_using_as_mediator'></a>
```cs
public class MediatorController : ControllerBase
{
    [HttpPost("/question")]
    public Task<Answer> Get(Question question, [FromServices] IMessageBus bus)
    {
        // All the real processing happens in Wolverine
        return bus.InvokeAsync<Answer>(question);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Samples/MediatorController.cs#L6-L18' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_as_mediator' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Optimized Minimal API Integration

While that strategy works and doesn't require Wolverine.Http at all, there's an optimized Minimal API approach in
Wolverine.HTTP to quickly build ASP.Net Core routes with Wolverine message handlers that bypasses some of the 
performance overhead of "classic mediator" usage.

The functionality is used from extension methods off of the ASP.Net Core [WebApplication](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.builder.webapplication?view=aspnetcore-7.0) class used in bootstrapping:

<!-- snippet: sample_optimized_mediator_usage -->
<a id='snippet-sample_optimized_mediator_usage'></a>
```cs
// Functional equivalent to MapPost(pattern, (command, IMessageBus) => bus.Invoke(command))
app.MapPostToWolverine<HttpMessage1>("/wolverine");
app.MapPutToWolverine<HttpMessage2>("/wolverine");
app.MapDeleteToWolverine<HttpMessage3>("/wolverine");

// Functional equivalent to MapPost(pattern, (command, IMessageBus) => bus.Invoke<IResponse>(command))
app.MapPostToWolverine<CustomRequest, CustomResponse>("/wolverine/request");
app.MapDeleteToWolverine<CustomRequest, CustomResponse>("/wolverine/request");
app.MapPutToWolverine<CustomRequest, CustomResponse>("/wolverine/request");
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Program.cs#L241-L253' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_optimized_mediator_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

With this mechanism, Wolverine is able to optimize the runtime function for Minimal API by eliminating IoC service locations
and some internal dictionary lookups compared to the "classic mediator" approach at the top.

This approach is potentially valuable for cases where you want to process a command or event message both through messaging
or direct invocation and also want to execute the same message through an HTTP endpoint. 

