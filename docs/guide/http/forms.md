# Working with Form Data <Badge type="tip" text="3.13" />

Wolverine will allow you to bind HTTP form data to a model type that is decorated with the ``[FromForm]`` attribute from
ASP.Net Core. 

Similar to the above usuage of `[FromQuery]` Wolverine also supports form parameters as input either directly as method parameters like shown here:

<!-- snippet: sample_using_string_value_as_form -->
<a id='snippet-sample_using_string_value_as_form'></a>
```cs
[WolverinePost("/form/string")]
public static string UsingForm([FromForm]string name) // name is from form data
{
    return name.IsEmpty() ? "Name is missing" : $"Name is {name}";
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/TestEndpoints.cs#L58-L66' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_string_value_as_form' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

And the corresponding test:


<!-- snippet: sample_form_value_usage -->
<a id='snippet-sample_form_value_usage'></a>
```cs
[Fact]
public async Task use_string_form_hit()
{
    var body = await Scenario(x =>
    {
        x.Post
            .FormData(new Dictionary<string,string>{
                ["name"] = "Magic"
            })
            .ToUrl("/form/string");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Name is Magic");
}

[Fact]
public async Task use_string_form_miss()
{
    var body = await Scenario(x =>
    {
        x.Post
            .FormData([])
            .ToUrl("/form/string");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Name is missing");
}

[Fact]
public async Task use_decimal_form_hit()
 {
    var body = await Scenario(x =>
    {
        x.WithRequestHeader("Accept-Language", "fr-FR");
        x.Post
            .FormData(new Dictionary<string,string> (){
                {"Amount", "42.1"}
            })
            .ToUrl("/form/decimal");
        x.Header("content-type").SingleValueShouldEqual("text/plain");
    });

    body.ReadAsText().ShouldBe("Amount is 42.1");
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/Wolverine.Http.Tests/using_form_parameters.cs#L478-L527' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_form_value_usage' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->


You can also use the FromForm attribute on a complex type, Wolverine will then attempt to bind all public properties or all parameters from the single default constructor with Form values:

<!-- snippet: sample_using_[fromform]_binding -->
<a id='snippet-sample_using_[fromform]_binding'></a>
```cs
[WolverinePost("/api/fromformbigquery")]
public static BigQuery Post([FromForm] BigQuery query) => query;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Forms/FormEndpoints.cs#L98-L101' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_[fromform]_binding' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Individual properties on the class can be aliased using ``[FromForm(Name = "aliased")]``
