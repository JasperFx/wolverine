using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

#region sample_using_openapi_attributes

public class SignupEndpoint
{
    // The first couple attributes are ASP.Net Core 
    // attributes that add OpenAPI metadata to this endpoint
    [Tags("Users")]
    [ProducesResponseType(204)]
    [WolverinePost("/users/sign-up")]
    public static IResult SignUp(SignUpRequest request)
    {
        return Results.NoContent();
    }
}

#endregion

public record SignUpRequest(string User, string Password);