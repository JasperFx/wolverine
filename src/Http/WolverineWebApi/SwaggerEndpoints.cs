using Marten;
using Wolverine.Http;

namespace WolverineWebApi;

// Just to test Swagger integration
[Tags("swagger")]
public class SwaggerEndpoints
{
    [WolverineGet("/swagger/users/{userId}")]
    public static Task<UserProfile> GetUserProfile(string userId, IQuerySession session)
    {
        return Task.FromResult(new UserProfile { Id = userId });
    }
}

public class UserProfile
{
    public string Id { get; set; }
}