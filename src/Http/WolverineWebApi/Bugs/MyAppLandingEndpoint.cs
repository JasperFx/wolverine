using Wolverine.Http;

namespace WolverineWebApi.Bugs;

public static class MyAppLandingEndpoint
{
    [WolverineGet("/api/myapp/registration-price")]
    public static decimal GetRegistrationPrice(int numberOfMembers)
    {
        decimal pricePerMember = numberOfMembers >= 500 ? 450 : 280;
        return pricePerMember * numberOfMembers;
    }
}