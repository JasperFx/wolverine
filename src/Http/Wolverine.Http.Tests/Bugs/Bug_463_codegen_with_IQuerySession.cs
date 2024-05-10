using Alba;
using IntegrationTests;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_463_codegen_with_IQuerySession
{
    [Fact]
    public async Task can_compile_without_issue()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Services.AddScoped<IUserService, UserService>();

        builder.Services.AddMarten(Servers.PostgresConnectionString)
            .IntegrateWithWolverine();

        builder.Host.UseWolverine(opts =>
        {
            opts.Discovery.IncludeAssembly(GetType().Assembly);
        });

        await using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        await host.Scenario(x =>
        {
            x.Put.Json(new RequestPasswordResetRequest("me@server.com")).ToUrl("/request-password-reset");
            x.StatusCodeShouldBe(204);
        });

        await host.Scenario(x =>
        {
            x.Post.Json(new SomeInput()).ToUrl("/bug463/underscore");
        });
    }
}

public interface IUserService
{
    Task<Guid?> GetUserIdByEmailAddress(string requestEmailAddress);
}

public class UserService : IUserService
{
    public UserService(IQuerySession session)
    {
    }

    public Task<Guid?> GetUserIdByEmailAddress(string requestEmailAddress)
    {
        return Task.FromResult<Guid?>(Guid.NewGuid());
    }
}

public sealed record RequestPasswordResetRequest(string EmailAddress);

public record SomeInput;

public static class RequestPasswordResetEndpoint
{
    [WolverinePost("/bug463/underscore")]
    public static string Post(SomeInput _)
    {
        return "got it";
    }

    [WolverinePut($"/request-password-reset")]
    public static async Task RequestPasswordReset(
        RequestPasswordResetRequest request,
        IMessageBus bus,
        IUserService userService)
    {
        var userId = await userService.GetUserIdByEmailAddress(request.EmailAddress);
        if (!userId.HasValue)
            return;

        var cmd = new RequestPasswordReset(userId.Value, request.EmailAddress);
        await bus.InvokeAsync(cmd);
    }
}

public sealed record RequestPasswordReset(Guid UserId, string EmailAddress);

public static class RequestPasswordResetHandler
{
    public static IEnumerable<object> Handle(RequestPasswordReset _)
    {
        yield break;
    }
}