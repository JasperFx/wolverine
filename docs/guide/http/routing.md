# Routing

::: warning
The route argument to method name matching is case-sensitive.
:::

Wolverine HTTP endpoints need to be decorated with one of the `[WolverineVerb("route")]` attributes
that expresses the routing argument path in standard ASP.Net Core syntax (i.e., the same as when using
MVC Core or Minimal API).

If a parameter argument to the HTTP handler method *exactly matches* a route argument, Wolverine will
treat that as a route argument and pass the route argument value at runtime from ASP.Net Core to your
handler method. To make that concrete, consider this simple case from the test suite:

<!-- snippet: sample_using_string_route_parameter -->
<a id='snippet-sample_using_string_route_parameter'></a>
```cs
[WolverineGet("/name/{name}")]
public static string SimpleStringRouteArgument(string name)
{
    return $"Name is {name}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L27-L35' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_string_route_parameter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the sample above, the `name` argument will be the value of the route argument
at runtime. Here's another example, but this time using a numeric value:

<!-- snippet: sample_using_numeric_route_parameter -->
<a id='snippet-sample_using_numeric_route_parameter'></a>
```cs
[WolverineGet("/age/{age}")]
public static string IntRouteArgument(int age)
{
    return $"Age is {age}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L37-L45' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_numeric_route_parameter' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

The following code snippet from `WolverineFx.Http` itself shows the valid route
parameter types that are supported at this time:

<!-- snippet: sample_supported_route_parameter_types -->
<a id='snippet-sample_supported_route_parameter_types'></a>
```cs
public static readonly Dictionary<Type, string> TypeOutputs = new()
{
    { typeof(bool), "bool" },
    { typeof(byte), "byte" },
    { typeof(sbyte), "sbyte" },
    { typeof(char), "char" },
    { typeof(decimal), "decimal" },
    { typeof(float), "float" },
    { typeof(short), "short" },
    { typeof(int), "int" },
    { typeof(double), "double" },
    { typeof(long), "long" },
    { typeof(ushort), "ushort" },
    { typeof(uint), "uint" },
    { typeof(ulong), "ulong" },
    { typeof(Guid), typeof(Guid).FullName! },
    { typeof(DateTime), typeof(DateTime).FullName! },
    { typeof(DateTimeOffset), typeof(DateTimeOffset).FullName! }
};
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http/CodeGen/RouteHandling.cs#L78-L100' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_supported_route_parameter_types' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
Wolverine will return a 404 status code if a route parameter cannot
be correctly parsed. So passing "ABC" into what is expected to be an
integer will result in a 404 response.
:::

## Route Name

You can add a name to the ASP.Net route with this property that is on all of the route definition attributes:

<!-- snippet: sample_using_route_name -->
<a id='snippet-sample_using_route_name'></a>
```cs
[WolverinePost("/named/route", RouteName = "NamedRoute")]
public string Post()
{
    return "Hello";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/NamedRouteEndpoint.cs#L7-L15' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_route_name' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->




