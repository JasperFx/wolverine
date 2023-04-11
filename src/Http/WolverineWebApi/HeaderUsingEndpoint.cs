using JasperFx.Core;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public class HeaderUsingEndpoint
{
    [WolverineGet("/headers/simple")]
    public string Get([FromHeader(Name = "x-wolverine")] string name)
    {
        return name;
    }    
    
    [WolverineGet("/headers/int")]
    public string Get([FromHeader(Name = "x-wolverine")] int number)
    {
        return (number * 2).ToString();
    }   

    [WolverineGet("/headers/accepts")]
    public string GetETag([FromHeader] string accepts)
    {
        return accepts;
    }
}