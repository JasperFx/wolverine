# Using HTTP Headers

While you can always just take in `HttpContext` as an argument to your HTTP endpoint method to read request headers,
there's some value in having your endpoint methods be [pure functions](https://en.wikipedia.org/wiki/Pure_function) 
to maximize the testability of your application code. Since reading header values and parsing those values into
specific .NET types is such a common use case, Wolverine has some middleware you can opt into to read the header values
and pass them into your endpoint methods using the `[Wolverine.Http.FromHeader]` attribute as shown from this sample
code from the Wolverine testing code:

<!-- snippet: sample_pushing_header_values_into_endpoint_methods -->
<a id='snippet-sample_pushing_header_values_into_endpoint_methods'></a>
```cs
// As of Wolverine 2.6, you can utilize header data in middleware
public static void Before([FromHeader(Name = "x-day")] string? day)
{
    Debug.WriteLine($"Day header is {day}");
    Day = day!; // This is for testing
}

[WolverineGet("/headers/simple")]
public string Get(
    // Find the request header with the supplied name and pass
    // it as the "name" parameter to this method at runtime
    [FromHeader(Name = "x-wolverine")]
    string name)
{
    return name;
}

[WolverineGet("/headers/int")]
public string Get(
    // Find the request header with the supplied name and pass
    // it as the "name" parameter to this method at runtime
    // If the attribute does not exist, Wolverine will pass
    // in the default value for the parameter type, in this case
    // 0
    [FromHeader(Name = "x-wolverine")] int number
)
{
    return (number * 2).ToString();
}

[WolverineGet("/headers/accepts")]
// In this case, push the string value for the "accepts" header
// right into the parameter based on the parameter name
public string GetETag([FromHeader] string accepts)
{
    return accepts;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/HeaderUsingEndpoint.cs#L15-L54' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_pushing_header_values_into_endpoint_methods' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

## Unparseable header values

From Wolverine 7.0, a `[FromHeader]` value that is *present but cannot be parsed* to the parameter type, such as
`x-wolverine: abc` on the `int number` parameter above, answers `400 Bad Request` with a `ProblemDetails` body naming
the header, as ASP.NET Core minimal APIs do. A header sent more than once binds as its comma-joined values (`"1,2"`) and
is rejected the same way. A *missing* header still binds the parameter's default value, and a `string` header is never
rejected. This is `WolverineHttpOptions.RejectUnparseableHeaderValues`, the header counterpart of
[`RejectUnparseableQueryValues`](./querystring):

```csharp
app.MapWolverineEndpoints(opts =>
{
    // true by default from Wolverine 7.0; false puts back the 6.x behavior, where an
    // unparseable header binds the parameter's default value and the request proceeds
    opts.RejectUnparseableHeaderValues = false;
});
```

It was `false` throughout Wolverine 6.x. `opts.RestoreV6Defaults()` in `UseWolverine()` turns it off too, and an
explicit setting wins over both. F# code generation does not support strict binding yet, so an application that
pre-generates F# code for an endpoint with a parsed header parameter has to set it to `false`.
