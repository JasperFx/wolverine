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
    public static string SimpleStringRouteArgument(string name, HttpContext context)
    {
        var data = context.GetRouteValue("name");
        return $"Name is {name}";
    }
}

public class Results
{
    public int Sum { get; set; }
    public int Product { get; set; }
}