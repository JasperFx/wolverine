using Marten;
using Wolverine.Http;
using Wolverine.Marten;

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

    [WolverinePost("/swagger/empty"), EmptyResponse]
    public static IMartenOp PostEmpty(CreateUserProfile command)
    {
        return MartenOps.Store(new UserProfile{Id = command.Name + Guid.NewGuid().ToString()});
    }
}

public record CreateUserProfile(string Name);

public class UserProfile
{
    public string Id { get; set; }
}