using System.Globalization;
using Marten;
using Wolverine.Http;
using JasperFx.Core;
using Microsoft.AspNetCore.Mvc;

namespace WolverineWebApi;

[SpecialModifyHttpChain]
public static class TestEndpoints
{
    [WolverineGet("/hello")]
    public static string Speak()
    {
        return "Hello";
    }

    [WolverineGet("/results/static")]
    public static ArithmeticResults FetchStaticResults()
    {
        return new ArithmeticResults
        {
            Sum = 3,
            Product = 4
        };
    }

    #region sample_using_string_route_parameter

    [WolverineGet("/name/{name}")]
    public static string SimpleStringRouteArgument(string name)
    {
        return $"Name is {name}";
    }

    #endregion

    #region sample_using_numeric_route_parameter

    [WolverineGet("/age/{age}")]
    public static string IntRouteArgument(int age)
    {
        return $"Age is {age}";
    }

    #endregion

    #region sample_using_string_value_as_query_string

    [WolverineGet("/querystring/string")]
    public static string UsingQueryString(string name) // name is from the query string
    {
        return name.IsEmpty() ? "Name is missing" : $"Name is {name}";
    }

    #endregion

    #region sample_using_string_value_as_form

    [WolverinePost("/form/string")]
    public static string UsingForm([FromForm]string name) // name is from form data
    {
        return name.IsEmpty() ? "Name is missing" : $"Name is {name}";
    }

    #endregion

    [WolverineGet("/querystring/int")]
    public static string UsingQueryStringParsing(Recorder recorder, int? age)
    {
        recorder.Actions.Add("got through query string usage");
        return $"Age is {age}";
    }

    [WolverinePost("/form/int")]
    public static string UsingFormParsing([NotBody]Recorder recorder, [FromForm]int? age)
    {
        recorder.Actions.Add("got through form usage");
        return $"Age is {age}";
    }
    

    [WolverineGet("/querystring/int/nullable")]
    public static string UsingQueryStringParsingNullable(int? age)
    {
        if (!age.HasValue)
        {
            return "Age is missing";
        }

        return $"Age is {age}";
    }


    [WolverinePost("/form/int/nullable")]
    public static string UsingFormParsingNullable([FromForm]int? age)
    {
        if (!age.HasValue)
        {
            return "Age is missing";
        }

        return $"Age is {age}";
    }
    
    [WolverineGet("/querystring/decimal")]
    public static string UseQueryStringParsing(Recorder recorder, decimal amount)
    {
        recorder.Actions.Add("Got through query string usage for decimal");

        return string.Format(CultureInfo.InvariantCulture, "Amount is {0}", amount);
    }

    [WolverinePost("/form/decimal")]
    public static string UseFormParsing([NotBody]Recorder recorder, [FromForm]decimal amount)
    {
        recorder.Actions.Add("Got through form usage for decimal");

        return string.Format(CultureInfo.InvariantCulture, "Amount is {0}", amount);
    }

    #region sample_simple_wolverine_http_endpoint

    [WolverinePost("/question")]
    public static ArithmeticResults PostJson(Question question)
    {
        return new ArithmeticResults
        {
            Sum = question.One + question.Two,
            Product = question.One * question.Two
        };
    }

    #endregion

    #region sample_simple_wolverine_http_endpoint_async

    [WolverinePost("/question2")]
    public static Task<ArithmeticResults> PostJsonAsync(Question question)
    {
        var results = new ArithmeticResults
        {
            Sum = question.One + question.Two,
            Product = question.One * question.Two
        };

        return Task.FromResult(results);
    }

    #endregion
}

public static class QuerystringCollectionEndpoints
{
    [WolverineGet("/querystring/collection/string")]
    public static string UsingStringCollection(List<string> collection)
    {
        return string.Join(",", collection);
    }

    [WolverineGet("/querystring/collection/int")]
    public static string UsingIntCollection(IList<int> collection)
    {
        return string.Join(",", collection);
    }

    [WolverineGet("/querystring/collection/guid")]
    public static string UsingGuidCollection(IReadOnlyList<Guid> collection)
    {
        return string.Join(",", collection);
    }

    [WolverineGet("/querystring/collection/enum")]
    public static string UsingEnumCollection(IEnumerable<Direction> collection)
    {
        return string.Join(",", collection);
    }
    
    
}


public static class FormCollectionEndpoints
{
    [WolverinePost("/form/collection/string")]
    public static string UsingStringCollection([FromForm]List<string> collection)
    {
        return string.Join(",", collection);
    }

    [WolverinePost("/form/collection/int")]
    public static string UsingIntCollection([FromForm]IList<int> collection)
    {
        return string.Join(",", collection);
    }

    [WolverinePost("/form/collection/guid")]
    public static string UsingGuidCollection([FromForm]IReadOnlyList<Guid> collection)
    {
        return string.Join(",", collection);
    }

    [WolverinePost("/form/collection/enum")]
    public static string UsingEnumCollection([FromForm]IEnumerable<Direction> collection)
    {
        return string.Join(",", collection);
    }
    
    
}

public class ArithmeticResults
{
    public int Sum { get; set; }
    public int Product { get; set; }
}

public class Question
{
    public int One { get; set; }
    public int Two { get; set; }
}

public enum Direction
{
    North, East, West, South
}