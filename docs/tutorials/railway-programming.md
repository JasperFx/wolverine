# Railway Programming with Wolverine (Kind Of)

::: tip
I'm sure a grizzled, experienced developer in your life has already told you this 
many times, but throwing and catching `Exceptions` in .NET code is pretty expensive in 
terms of performance.
:::

[Railway Programming](https://fsharpforfunandprofit.com/rop/) is an idea that came out of the F#
community as a way to develop for "sad path" exception cases without having to resort to
throwing .NET `Exceptions` as a way of doing flow control by chaining together functions in 
such a way that it's relatively easy to abort workflows is preliminary steps are invalid. 

As with just about anything in software development, Railway Programming can be abused or
just not be terribly ideal in certain areas. Also see [Against Railway-Oriented Programming](https://fsharpforfunandprofit.com/posts/against-railway-oriented-programming/) 
from its originator just about where it's not a great fit.

Most .NET implementations of Railway Programming that this author has seen involve using
some kind of custom `Result` type that denotes in a standard way if the processing should continue
or stop. [Andrew Lock](https://andrewlock.net/about/) wrote a series about doing this in his series
[Working with the result pattern](https://andrewlock.net/working-with-the-result-pattern-part-1-replacing-exceptions-as-control-flow/).

::: warning
Some teams have tried to do Railway Programming by using a mediator library where each
message handler returns some kind of custom `Result` value, then try to chain complex workflows by calling
a separate message handler for each step. The Wolverine team **very strongly recommends against this approach** as it
creates a lot of code ceremony and flat out noise code while detracting from both your ability to reason about the code
in your system. That approach can very easily create severe performance problems by being "chatty" in its interactions with backing
databases and generally making it hard for teams to even see the relationship between system inputs and what database calls are being made.
:::

Wolverine has some direct support for a quasi-Railway Programming approach by moving validation
or data loading steps prior to the main message handler or HTTP endpoint logic. Let's jump into
a quick sample that works with either message handlers or HTTP endpoints using the built in [HandlerContinuation](/guide/handlers/middleware.html#conditionally-stopping-the-message-handling) enum:

```csharp
public static class ShipOrderHandler
{
    // This would be called first
    public static async Task<(HandlerContinuation, Order?, Customer?)> LoadAsync(ShipOrder command, IDocumentSession session)
    {
        var order = await session.LoadAsync<Order>(command.OrderId);
        if (order == null)
        {
            return (HandlerContinuation.Stop, null, null);
        }

        var customer = await session.LoadAsync<Customer>(command.CustomerId);

        return (HandlerContinuation.Continue, order, customer);
    }

    // The main method becomes the "happy path", which also helps simplify it
    public static IEnumerable<object> Handle(ShipOrder command, Order order, Customer customer)
    {
        // use the command data, plus the related Order & Customer data to
        // "decide" what action to take next

        yield return new MailOvernight(order.Id);
    }
}
```

By naming convention (but you can override the method naming with attributes as you see fit), Wolverine will try to generate
code that will call methods named `Before/Validate/Load(Async)` before the main message handler method or the HTTP endpoint method.
You can use this [compound handler](/guide/handlers/#compound-handlers) approach to do set up work like loading data required by business logic in the main 
method or in this case, as validation logic that can stop further processing based on failed validation or data requirements or
system state. Some Wolverine users like using these method to keep the main methods relatively simple and focused on the "happy path" and business
logic in pure functions that are easier to unit test in isolation. 

By returning a `HandlerContinuation` value either by itself or as part of a tuple returned by a `Before`, `Validate`, or `LoadAsync` method, you can
direct Wolverine to stop all other processing. 

You have more specialized ways of doing that in HTTP endpoints by using the `ProblemDetails` specification to stop
processing like this example that uses a `Validate()` method to potentially stop processing with a descriptive 400 and error message:

<!-- snippet: sample_CategoriseIncident -->
<a id='snippet-sample_CategoriseIncident'></a>
```cs
public record CategoriseIncident(
    IncidentCategory Category,
    Guid CategorisedBy,
    int Version
);

public static class CategoriseIncidentEndpoint
{
    // This is Wolverine's form of "Railway Programming"
    // Wolverine will execute this before the main endpoint,
    // and stop all processing if the ProblemDetails is *not*
    // "NoProblems"
    public static ProblemDetails Validate(Incident incident)
    {
        return incident.Status == IncidentStatus.Closed 
            ? new ProblemDetails { Detail = "Incident is already closed" } 
            
            // All good, keep going!
            : WolverineContinue.NoProblems;
    }
    
    // This tells Wolverine that the first "return value" is NOT the response
    // body
    [EmptyResponse]
    [WolverinePost("/api/incidents/{incidentId:guid}/category")]
    public static IncidentCategorised Post(
        // the actual command
        CategoriseIncident command, 
        
        // Wolverine is generating code to look up the Incident aggregate
        // data for the event stream with this id
        [Aggregate("incidentId")] Incident incident)
    {
        // This is a simple case where we're just appending a single event to
        // the stream.
        return new IncidentCategorised(incident.Id, command.Category, command.CategorisedBy);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Samples/IncidentService/IncidentService/CategoriseIncident.cs#L8-L49' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_CategoriseIncident' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The value `WolverineContinue.NoProblems` tells Wolverine that everything is good, full speed ahead. Anything else will write the `ProblemDetails`
value out to the response, return a 400 status code (or whatever you decide to use), and stop processing. Returning a `ProblemDetails`
object hopefully makes these filter methods easy to unit test themselves. 

You can also use the AspNetCore `IResult` as another formally supported "result" type in these filter methods like this
shown below:

<!-- snippet: sample_using_continue_result_as_filter -->
<a id='snippet-sample_using_continue_result_as_filter'></a>
```cs
public static class ExamineFirstHandler
{
    public static bool DidContinue { get; set; }
    
    public static IResult Before([Entity] Todo2 todo)
    {
        return todo != null ? WolverineContinue.Result() : Results.Empty;
    }

    [WolverinePost("/api/todo/examinefirst")]
    public static void Handle(ExamineFirst command) => DidContinue = true;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L189-L204' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_continue_result_as_filter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In this case, the "special" value `WolverineContinue.Result()` tells Wolverine to keep going, otherwise, Wolverine will 
execute the `IResult` returned from one of these filter methods and stop all other processing for the HTTP request.

