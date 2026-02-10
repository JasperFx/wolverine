# Using AsParameters <Badge type="tip" text="3.13" />

::: warning
When you use ``[AsParameters]``, you can read HTTP form data or deserialize a request body as JSON, but **not both at the same time**
and Wolverine will happily throw an exception telling you so if you try to do this. 
:::

::: tip
Use Wolverine's pre-generated code to understand exactly how Wolverine is processing any model object decorated with 
``[AsParameters]``
:::

Wolverine supports the ASP.Net Core AsParameters attribute usage for complex binding of a mixed bag of HTTP information
including headers, form data elements, route arguments, the request body, IoC services to a single input model using
the ASP.Net Core ``[AsParameters]`` attribute as a marker.

See the [Microsoft documentation on AsParameters](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis/parameter-binding?view=aspnetcore-9.0) for more background.

Below is a sample from our test suite showing what is possible for query string and header values:

<!-- snippet: sample_using_as_parameters_binding -->
<a id='snippet-sample_using_as_parameters_binding'></a>
```cs
public static class AsParametersEndpoints{
    [WolverinePost("/api/asparameters1")]
    public static AsParametersQuery Post([AsParameters] AsParametersQuery query)
    {
        return query;
    }
}

public class AsParametersQuery{
    [FromQuery]
    public Direction EnumFromQuery{ get; set; }
    [FromForm]
    public Direction EnumFromForm{ get; set; }

    public Direction EnumNotUsed{get;set;}

    [FromQuery]
    public string StringFromQuery { get; set; }
    [FromForm]
    public string StringFromForm { get; set; }
    public string StringNotUsed { get; set; }
    [FromQuery]
    public int IntegerFromQuery { get; set; }
    [FromForm]
    public int IntegerFromForm { get; set; }
    public int IntegerNotUsed { get; set; }
    [FromQuery]
    public float FloatFromQuery { get; set; }
    [FromForm]
    public float FloatFromForm { get; set; }
    public float FloatNotUsed { get; set; }
    [FromQuery]
    public bool BooleanFromQuery { get; set; }
    [FromForm]
    public bool BooleanFromForm { get; set; }
    public bool BooleanNotUsed { get; set; }
    
    [FromHeader(Name = "x-string")]
    public string StringHeader { get; set; }

    [FromHeader(Name = "x-number")] public int NumberHeader { get; set; } = 5;
    
    [FromHeader(Name = "x-nullable-number")]
    public int? NullableHeader { get; set; }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L128-L174' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_as_parameters_binding' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the corresponding test case for utilizing this:

<!-- snippet: sample_using_asparameters_test -->
<a id='snippet-sample_using_asparameters_test'></a>
```cs
var result = await Host.Scenario(x => x
    .Post
    .FormData(new Dictionary<string, string>
    {
        { "EnumFromForm", "east" },
        { "StringFromForm", "string2" },
        { "IntegerFromForm", "2" },
        { "FloatFromForm", "2.2" },
        { "BooleanFromForm", "true" },
        { "StringNotUsed", "string3" }
    }).QueryString("EnumFromQuery", "west")
    .QueryString("StringFromQuery", "string1")
    .QueryString("IntegerFromQuery", "1")
    .QueryString("FloatFromQuery", "1.1")
    .QueryString("BooleanFromQuery", "true")
    .QueryString("IntegerNotUsed", "3")
    .ToUrl("/api/asparameters1")
);
var response = result.ReadAsJson<AsParametersQuery>();
response.EnumFromForm.ShouldBe(Direction.East);
response.StringFromForm.ShouldBe("string2");
response.IntegerFromForm.ShouldBe(2);
response.FloatFromForm.ShouldBe(2.2f);
response.BooleanFromForm.ShouldBeTrue();
response.EnumFromQuery.ShouldBe(Direction.West);
response.StringFromQuery.ShouldBe("string1");
response.IntegerFromQuery.ShouldBe(1);
response.FloatFromQuery.ShouldBe(1.1f);
response.BooleanFromQuery.ShouldBeTrue();
response.EnumNotUsed.ShouldBe(default);
response.StringNotUsed.ShouldBe(default);
response.IntegerNotUsed.ShouldBe(default);
response.FloatNotUsed.ShouldBe(default);
response.BooleanNotUsed.ShouldBe(default);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/asparameters_binding.cs#L18-L55' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_asparameters_test' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Wolverine.HTTP is also able to support `[FromServices]`, `[FromBody]`, and `[FromRoute]` bindings as well
as shown in this sample from the tests:

<!-- snippet: sample_using_as_parameter_for_services_and_body -->
<a id='snippet-sample_using_as_parameter_for_services_and_body'></a>
```cs
public class AsParameterBody
{
    public string Name { get; set; }
    public Direction Direction { get; set; }
    public int Distance { get; set; }
}

public class AsParametersQuery2
{
    // We do a check inside of an HTTP endpoint that this works correctly
    [FromServices, JsonIgnore]
    public IDocumentStore Store { get; set; }
    
    [FromBody]
    public AsParameterBody Body { get; set; }
    
    [FromRoute]
    public string Id { get; set; }
    
    [FromRoute]
    public int Number { get; set; }
}

public static class AsParametersEndpoints2{
    [WolverinePost("/asp2/{id}/{number}")]
    public static AsParametersQuery2 Post([AsParameters] AsParametersQuery2 query)
    {
        // Just proving the service binding works
        query.Store.ShouldBeOfType<DocumentStore>();
        return query;
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L176-L211' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_as_parameter_for_services_and_body' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And lastly, you can use C# records or really just any constructor function as well
and decorate parameters like so:

<!-- snippet: sample_as_parameter_record -->
<a id='snippet-sample_as_parameter_record'></a>
```cs
public record AsParameterRecord(
    [FromRoute] string Id,
    [FromQuery] int Number,
    [FromHeader(Name = "x-direction")] Direction Direction,
    [FromForm(Name = "test")] bool IsTrue);

public static class AsParameterRecordEndpoint
{
    [WolverinePost("/asparameterrecord/{Id}")]
    public static AsParameterRecord Post([AsParameters] AsParameterRecord input) => input;
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L213-L227' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_as_parameter_record' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


The [Fluent Validation middleware](./fluentvalidation) for Wolverine.HTTP is able to validate against request types
bound with `[AsParameters]`:

<!-- snippet: sample_using_fluent_validation_with_AsParameters -->
<a id='snippet-sample_using_fluent_validation_with_asparameters'></a>
```cs
public static class ValidatedAsParametersEndpoint
{
    [WolverineGet("/asparameters/validated")]
    public static string Get([AsParameters] ValidatedQuery query)
    {
        return $"{query.Name} is {query.Age}";
    }
}

public class ValidatedQuery
{
    [FromQuery]
    public string? Name { get; set; }
    
    public int Age { get; set; }

    public class ValidatedQueryValidator : AbstractValidator<ValidatedQuery>
    {
        public ValidatedQueryValidator()
        {
            RuleFor(x => x.Name).NotNull();
        }
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L230-L257' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_fluent_validation_with_asparameters' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->
