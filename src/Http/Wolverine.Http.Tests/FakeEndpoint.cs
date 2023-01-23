using Microsoft.AspNetCore.Mvc;
using Wolverine.Http.Tests;

namespace TestEndpoints;

public class FakeEndpoint
{
    [HttpGet("/hello")]
    public string SayHello()
    {
        return "Hello";
    }
    
    [HttpGet("/hello/async")]
    public Task<string> SayHelloAsync()
    {
        return Task.FromResult("Hello");
    }
    
    [HttpGet("/hello/async2")]
    public ValueTask<string> SayHelloAsync2()
    {
        return ValueTask.FromResult("Hello");
    }

    [HttpPost("/go")]
    public void Go()
    {
        
    }

    [HttpPost("/go/async")]
    public Task GoAsync()
    {
        return Task.CompletedTask;
    }
    
    [HttpPost("/go/async2")]
    public ValueTask GoAsync2()
    {
        return ValueTask.CompletedTask;
    }

    [HttpGet("/response")]
    public BigResponse GetResponse()
    {
        return new BigResponse();
    }
    
    [HttpGet("/response2")]
    public Task<BigResponse> GetResponseAsync()
    {
        return Task.FromResult(new BigResponse());
    }
    
        
    [HttpGet("/response3")]
    public ValueTask<BigResponse> GetResponseAsync2()
    {
        return ValueTask.FromResult(new BigResponse());
    }
}