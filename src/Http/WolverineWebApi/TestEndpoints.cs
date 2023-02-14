using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

[Special]
public static class TestEndpoints
{
    [WolverineGet("/hello")]
    public static string Speak()
    {
        return "Hello";
    }

    [WolverineGet("/results/static")]
    public static Results FetchStaticResults()
    {
        return new Results
        {
            Sum = 3,
            Product = 4
        };
    }

    [WolverineGet("/name/{name}")]
    public static string SimpleStringRouteArgument(string name)
    {
        return $"Name is {name}";
    }
    
    [WolverineGet("/age/{age}")]
    public static string IntRouteArgument(int age)
    {
        return $"Age is {age}";
    }

    [WolverineGet("/querystring/string")]
    public static string UsingQueryString(string name)
    {
        return name.IsEmpty() ? "Name is missing" : $"Name is {name}";
    }
    
    [WolverineGet("/querystring/int")]
    public static string UsingQueryStringParsing(int age)
    {
        return $"Age is {age}";
    }
    
    [WolverineGet("/querystring/int/nullable")]
    public static string UsingQueryStringParsingNullable(int? age)
    {
        if (!age.HasValue) return "Age is missing";
        return $"Age is {age}";
    }

    [WolverinePost("/question")]
    public static Results PostJson(Question question)
    {
        return new Results
        {
            Sum = question.One + question.Two,
            Product = question.One * question.Two
        };
    }
}

public class Results
{
    public int Sum { get; set; }
    public int Product { get; set; }
}

public class Question
{
    public int One { get; set; }
    public int Two { get; set; }
}