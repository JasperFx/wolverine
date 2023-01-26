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
}

public class Results
{
    public int Sum { get; set; }
    public int Product { get; set; }
}