# Working with QueryString

::: warning
`WolverineHttpOptions.RejectUnparseableQueryValues` defaults to **true** from Wolverine 7.0 (it was
`false` throughout 6.x). A query string value that is *present but unparseable* answers
`400 Bad Request` with a `ProblemDetails` body naming the offending parameter, as MVC and minimal
APIs do.

Setting it to `false` (or calling `opts.RestoreV6Defaults()` in `UseWolverine`) puts back the 6.x
lenient binding, where the unparseable value binds the parameter's default and **the request
proceeds**:

| Request | Parameter | Result |
| --- | --- | --- |
| `?page=abc` | `int page` | runs with `page = 0` |
| `?since=yesterday` | `DateTimeOffset since` | runs with `default` |
| `?ids=1,x,3` | `int[] ids` | runs with the bad element dropped |

Nothing is logged for the individual request, so the server sees what looks like a perfectly valid
call — which is what makes "the API returned the wrong page" so hard to chase. Wolverine logs one
warning at startup when an application binds parsed query parameters and the flag is off.

```csharp
app.MapWolverineEndpoints(opts =>
{
    // Only if you depend on the 6.x lenient binding
    opts.RejectUnparseableQueryValues = false;
});
```

A **missing** query string value still binds the parameter's default in either mode; this governs
only values that are present and cannot be parsed. See
[Strict Query String Binding](./as-parameters#strict-query-string-binding).
:::

::: tip
Wolverine can handle both nullable types and the primitive values here, so `int` and `int?` are
both valid.
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L48-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_string_value_as_query_string' title='Start of snippet'>anchor</a></sup>
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

    var text = await body.ReadAsTextAsync();
    text.ShouldBe("Name is Magic");
}

[Fact]
public async Task use_string_querystring_miss()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/string");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    var text = await body.ReadAsTextAsync();
    text.ShouldBe("Name is missing");
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

    var text = await body.ReadAsTextAsync();
    text.ShouldBe("Amount is 42.1");
}

[Fact]
public async Task use_decimal_fromquery_hit()
{
    // GH-3586 follow-up: an explicit [FromQuery] on a scalar decimal used to throw at endpoint
    // discovery ("System.Decimal has multiple constructors"). It now binds from its own key.
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/decimal2?value=42.1");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    (await body.ReadAsTextAsync()).ShouldBe("42.1");
}

[Fact]
public async Task use_nullable_decimal_fromquery_hit()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/decimal2/nullable?value=42.1");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    (await body.ReadAsTextAsync()).ShouldBe("42.1");
}

[Fact]
public async Task use_nullable_decimal_fromquery_miss()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/decimal2/nullable");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    (await body.ReadAsTextAsync()).ShouldBe("Value is missing");
}

[Fact]
public async Task use_enum_fromquery_hit_with_string_name()
{
    // Enum values arrive as their string name on the wire, not as an integer.
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/enum2?value=North");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    (await body.ReadAsTextAsync()).ShouldBe("North");
}

[Fact]
public async Task use_enum_fromquery_hit_with_lowercase_string_name()
{
    // ...and the name parse is case-insensitive.
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/enum2?value=west");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    (await body.ReadAsTextAsync()).ShouldBe("West");
}

[Fact]
public async Task use_nullable_enum_fromquery_hit_with_string_name()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/enum2/nullable?value=South");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    (await body.ReadAsTextAsync()).ShouldBe("South");
}

[Fact]
public async Task use_nullable_enum_fromquery_miss()
{
    var body = await Scenario(x =>
    {
        x.Get.Url("/querystring/enum2/nullable");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    (await body.ReadAsTextAsync()).ShouldBe("Value is missing");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/using_querystring_parameters.cs#L616-L745' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_query_string_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


## [FromQuery] Binding <Badge type="tip" text="3.12" />

Wolverine can support the [FromQueryAttribute](https://learn.microsoft.com/en-us/dotnet/api/microsoft.aspnetcore.mvc.fromqueryattribute?view=aspnetcore-9.0) binding similar to MVC Core or Minimal API. 
Let's say that you have a GET endpoint where you may want to use a series of non-mandatory querystring values for a query, and
it would be convenient to have Wolverine just let you declare a single .NET type for all the optional query string values that
will be filled with any of the matching query string parameters like this sample:

<!-- snippet: sample_using_[FromQuery]_binding -->
<a id='snippet-sample_using_[FromQuery]_binding'></a>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Marten/Orders.cs#L421-L461' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_[FromQuery]_binding' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Because we've used the `[FromQuery]` attribute on a parameter argument that's not a simple type, Wolverine is trying to bind
the query string values to each public property of the `OrderQuery` object being passed in as an argument to `QueryOrdersEndpoint.Query()`.

Here's the code that Wolverine generates around the method signature above (warning, it's ugly code):

```cs
public sealed class GET_api_orders_query : HttpHandler
{
    private readonly WolverineHttpOptions _wolverineHttpOptions;
    private readonly OutboxedSessionFactory _outboxedSessionFactory;
    private readonly IWolverineRuntime _wolverineRuntime;

    public GET_api_orders_query(WolverineHttpOptions wolverineHttpOptions, OutboxedSessionFactory outboxedSessionFactory, IWolverineRuntime wolverineRuntime) : base(wolverineHttpOptions)
    {
        _wolverineHttpOptions = wolverineHttpOptions;
        _outboxedSessionFactory = outboxedSessionFactory;
        _wolverineRuntime = wolverineRuntime;
    }

    public override async Task Handle(HttpContext httpContext)
    {
        var messageContext = new MessageContext(_wolverineRuntime);
        UserNameMiddleware.Apply(httpContext, messageContext);
        RequestIdMiddleware.Apply(httpContext, messageContext);
        // Building the Marten session
        await using var documentSession = _outboxedSessionFactory.OpenSession(messageContext);
        // (Open Telemetry tagging omitted)
        // Binding QueryString values to the argument marked with [FromQuery]
        var orderQuery = new OrderQuery();
        string PageSize_rawValue = httpContext.Request.Query["PageSize"];

        if (PageSize_rawValue != null && int.TryParse(PageSize_rawValue, CultureInfo.InvariantCulture, out var PageSize))
        {
            orderQuery.PageSize = PageSize;
        }

        string PageNumber_rawValue = httpContext.Request.Query["PageNumber"];

        if (PageNumber_rawValue != null && int.TryParse(PageNumber_rawValue, CultureInfo.InvariantCulture, out var PageNumber))
        {
            orderQuery.PageNumber = PageNumber;
        }

        string HasShipped_rawValue = httpContext.Request.Query["HasShipped"];

        if (bool.TryParse(HasShipped_rawValue, out var HasShipped_parsed))
        {
            orderQuery.HasShipped = HasShipped_parsed;
        }

        
        // The actual HTTP request handler execution
        var pagedListOfOrder_response = await QueryOrdersEndpoint.Query(orderQuery, ((IQuerySession)documentSession), httpContext.RequestAborted).ConfigureAwait(false);

        // Writing the response body to JSON because this was the first 'return variable' in the method signature
        await WriteJsonAsync(httpContext, pagedListOfOrder_response, 404);
    }
}
```

::: tip
That's what Wolverine 7.0 generates for this endpoint today, lightly trimmed. To see the real thing for your own endpoints, run `dotnet run -- codegen preview`.

The sample application turns `RejectUnparseableQueryValues` off, so the code above just skips a value it can't parse.
With the 7.0 default, that value answers a `400` instead, as described below.
:::

Note there are some limitations of this approach in Wolverine:

* Wolverine can use *either* a class that has a single constructor with arguments (like a `record` type) or a class with a public, default constructor and public settable properties but not have *both* a constructor with arguments and settable properties!
* The types marked as `[FromQuery]` must be public, as well as any properties you want to bind
* The binding supports array types, but know that you will always get an empty array as the value even with no matching query string values
* Likewise, `string` values will be null if there is no query string
* For any kind of parsed data (`Guid`, numbers, dates, boolean values, enums), Wolverine will not set any value on public setters if there is no matching querystring value. A value that is present but cannot be parsed answers a `400` by default from Wolverine 7.0, or is skipped the same way if you turn `RejectUnparseableQueryValues` off

## [AsParameters] Binding <Badge type="tip" text="3.13" />

Also see the [AsParameters](./as-parameters) binding. 
