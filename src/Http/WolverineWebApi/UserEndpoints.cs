using Marten;
using Microsoft.AspNetCore.Identity;
using Wolverine.Attributes;
using Wolverine.Http;

namespace WolverineWebApi;

public class UserEndpoints;

public record UserId(Guid Id);

public class Trainer
{
    public Guid Id { get; set; }
}

public static class UserIdMiddleware
{
    public static UserId LoadAsync(HttpContext context)
    {
        return new UserId(Guid.NewGuid());
    }
}

public class TrainerDelete
{

    [Tags("Trainer")]
    [WolverineDelete("/api/trainer")]
    [Middleware(typeof(UserIdMiddleware))]
    public async Task<IResult> Delete([NotBody]UserId userId, IDocumentSession session, CancellationToken ct)
    {
        if (await session.LoadAsync<Trainer>(userId.Id, ct) != null)
        {
            session.HardDelete<Trainer>(userId.Id);
            await session.SaveChangesAsync(ct);
        }

        IdentityResult userDelete = new IdentityResult(){Errors = {  }};

        if (userDelete.Errors.Any())
        {
            return Results.Problem(userDelete.Errors.ToString());
        }

        return userDelete.Succeeded ? Results.NoContent() : Results.Problem("Unable to delete trainer", statusCode:400);
    }
}