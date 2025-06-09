# Working with QueryString

::: tip
Wolverine can handle both nullable types and the primitive values here. So
`int` and `int?` are both valid. In all cases, if the query string does not exist -- or
cannot be parsed -- the value passed to your method will be the `default` for whatever that
type is.
:::

Wolverine supports passing query string values to your HTTP method arguments for
the exact same set of value types supported for route arguments. In this case,
Wolverine treats any value type parameter where the parameter name does not
match a route argument name as coming from the HTTP query string.

When Wolverine does the runtime matching, it's using the exact parameter name as the
query string key. Here's a quick sample:

<!-- snippet: sample_using_string_value_as_query_string -->
<a id='snippet-sample_using_string_value_as_query_string'></a>
```cs
[WolverineGet("/querystring/string")]
public static string UsingQueryString(string name) // name is from the query string
{
    return name.IsEmpty() ? "Name is missing" : $"Name is {name}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L48-L56' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_string_value_as_query_string' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the corresponding tests:

<!-- snippet: sample_query_string_usage -->
<a id='snippet-sample_query_string_usage'></a>
```cs
[Fact]
public async Task use_string_querystring_hit()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/string?name=Magic");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Name is Magic");
}

[Fact]
public async Task use_string_querystring_miss()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/string");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Name is missing");
}

[Fact]
public async Task use_decimal_querystring_hit()
{
    var body = await Scenario(x =>
    {
        x.WithRequestHeader("Accept-Language", "fr-FR");
        x.Get.Url("/querystring/decimal?amount=42.1");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Amount is 42.1");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/using_querystring_parameters.cs#L449-L488' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_query_string_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## [FromQuery] Binding <Badge type="tip" text="3.12" />

Wolverine can support the [FromQueryAttribute](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.fromqueryattribute?view=aspnetcore-9.0) binding similar to MVC Core or Minimal API. 
Let's say that you have a GET endpoint where you may want to use a series of non-mandatory querystring values for a query, and
it would be convenient to have Wolverine just let you declare a single .NET type for all the optional query string values that
will be filled with any of the matching query string parameters like this sample:

<!-- snippet: sample_using_[FromQuery]_binding -->
<a id='snippet-sample_using_[fromquery]_binding'></a>
```cs
// If you want every value to be optional, use public, settable
// properties and a no-arg public constructor
public class OrderQuery
{
    public int PageSize { get; set; } = 10;
    public int PageNumber { get; set; } = 1;
    public bool? HasShipped { get; set; }
}

// Or -- and I'm not sure how useful this really is, use a record:
public record OrderQueryAlternative(int PageSize, int PageNumber, bool HasShipped);

public static class QueryOrdersEndpoint
{
    [WolverineGet("/api/orders/query")]
    public static Task<IPagedList<Order>> Query(
        // This will be bound from query string values in the HTTP request
        [FromQuery] OrderQuery query, 
        IQuerySession session,
        CancellationToken token)
    {
        IQueryable<Order> queryable = session.Query<Order>()
            // Just to make the paging deterministic
            .OrderBy(x => x.Id);

        if (query.HasShipped.HasValue)
        {
            queryable = query.HasShipped.Value 
                ? queryable.Where(x => x.Shipped.HasValue) 
                : queryable.Where(x => !x.Shipped.HasValue);
        }

        // Marten specific Linq helper
        return queryable.ToPagedListAsync(query.PageNumber, query.PageSize, token);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L310-L350' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_[fromquery]_binding' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Because we've used the `[FromQuery]` attribute on a parameter argument that's not a simple type, Wolverine is trying to bind
the query string values to each public property of the `OrderQuery` object being passed in as an argument to `QueryOrdersEndpoint.Query()`.

Here's the code that Wolverine generates around the method signature above (warning, it's ugly code):

```csharp
// <auto-generated/>
#pragma warning disable
using Microsoft.AspNetCore.Routing;
using System;
using System.Linq;
using Wolverine.Http;
using Wolverine.Marten.Publishing;
using Wolverine.Runtime;

namespace Internal.Generated.WolverineHandlers
{
    // START: GET_api_orders_query
    public class GET_api_orders_query : Wolverine.Http.HttpHandler
    {
        private readonly Wolverine.Http.WolverineHttpOptions _wolverineHttpOptions;
        private readonly Wolverine.Runtime.IWolverineRuntime _wolverineRuntime;
        private readonly Wolverine.Marten.Publishing.OutboxedSessionFactory _outboxedSessionFactory;

        public GET_api_orders_query(Wolverine.Http.WolverineHttpOptions wolverineHttpOptions, Wolverine.Runtime.IWolverineRuntime wolverineRuntime, Wolverine.Marten.Publishing.OutboxedSessionFactory outboxedSessionFactory) : base(wolverineHttpOptions)
        {
            _wolverineHttpOptions = wolverineHttpOptions;
            _wolverineRuntime = wolverineRuntime;
            _outboxedSessionFactory = outboxedSessionFactory;
        }



        public override async System.Threading.Tasks.Task Handle(Microsoft.AspNetCore.Http.HttpContext httpContext)
        {
            var messageContext = new Wolverine.Runtime.MessageContext(_wolverineRuntime);
            // Building the Marten session
            await using var querySession = _outboxedSessionFactory.QuerySession(messageContext);
            // Binding QueryString values to the argument marked with [FromQuery]
            var orderQuery = new WolverineWebApi.Marten.OrderQuery();
            if (int.TryParse(httpContext.Request.Query["PageSize"], System.Globalization.CultureInfo.InvariantCulture, out var PageSize)) orderQuery.PageSize = PageSize;
            if (int.TryParse(httpContext.Request.Query["PageNumber"], System.Globalization.CultureInfo.InvariantCulture, out var PageNumber)) orderQuery.PageNumber = PageNumber;
            if (bool.TryParse(httpContext.Request.Query["HasShipped"], out var HasShipped)) orderQuery.HasShipped = HasShipped;
            
            // The actual HTTP request handler execution
            var pagedList_response = await WolverineWebApi.Marten.QueryOrdersEndpoint.Query(orderQuery, querySession, httpContext.RequestAborted).ConfigureAwait(false);

            // Writing the response body to JSON because this was the first 'return variable' in the method signature
            await WriteJsonAsync(httpContext, pagedList_response);
        }

    }

    // END: GET_api_orders_query
    
    
}

```

Note there are some limitations of this approach in Wolverine:

* Wolverine can use *either* a class that has a single constructor with arguments (like a `record` type) or a class with a public, default constructor and public settable properties but not have *both* a constructor with arguments and settable properties!
* The types marked as `[FromQuery]` must be public, as well as any properties you want to bind
* The binding supports array types, but know that you will always get an empty array as the value even with no matching query string values
* Likewise, `string` values will be null if there is no query string
* For any kind of parsed data (`Guid`, numbers, dates, boolean values, enums), Wolverine will not set any value on public setters if there is either no matching querystring value or the querystring value cannot be parsed

## [AsParameters] Binding <Badge type="tip" text="3.13" />

Also see the [AsParameters](./as-parameters) binding. 
