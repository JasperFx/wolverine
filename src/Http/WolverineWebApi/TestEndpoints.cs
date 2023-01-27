using Marten;
using Microsoft.AspNetCore.Mvc;

namespace WolverineWebApi;

public static class TestEndpoints
{
    [HttpGet("/hello")]
    public static string Speak()
    {
        return "Hello";
    }

    [HttpGet("/results/static")]
    public static Results FetchStaticResults()
    {
        return new Results
        {
            Sum = 3,
            Product = 4
        };
    }

    [HttpGet("/name/{name}")]
    public static string SimpleStringRouteArgument(string name)
    {
        return $"Name is {name}";
    }
    
    [HttpGet("/age/{age}")]
    public static string IntRouteArgument(int age)
    {
        return $"Age is {age}";
    }

    [HttpGet("/querystring/string")]
    public static string UsingQueryString(string name)
    {
        return name.IsEmpty() ? "Name is missing" : $"Name is {name}";
    }
    
    [HttpGet("/querystring/int")]
    public static string UsingQueryStringParsing(int age)
    {
        return $"Age is {age}";
    }
    
    [HttpGet("/querystring/int/nullable")]
    public static string UsingQueryStringParsingNullable(int? age)
    {
        if (!age.HasValue) return "Age is missing";
        return $"Age is {age}";
    }
}

public class Results
{
    public int Sum { get; set; }
    public int Product { get; set; }
}