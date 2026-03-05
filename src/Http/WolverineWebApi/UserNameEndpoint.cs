using Wolverine;
using Wolverine.Http;

namespace WolverineWebApi;

public static class UserNameEndpoint
{
    [WolverineGet("/user/name")]
    public static string GetUserName(IMessageContext context)
    {
        return context.UserName ?? "NONE";
    }
}
