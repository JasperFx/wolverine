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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L47-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_string_value_as_query_string' title='Start of snippet'>anchor</a></sup>
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
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/using_querystring_parameters.cs#L267-L306' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_query_string_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
