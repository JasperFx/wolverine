using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;

namespace WolverineWebApi;

public class SignupEndpoint
{
    [Tags("Users")]
    [WolverinePost("/users/sign-up")]
    [ProducesResponseType(204)]
    public static IResult SignUp(SignUpRequest request)
    {
        return Microsoft.AspNetCore.Http.Results.NoContent();
    }
}

public record SignUpRequest(string User, string Password);