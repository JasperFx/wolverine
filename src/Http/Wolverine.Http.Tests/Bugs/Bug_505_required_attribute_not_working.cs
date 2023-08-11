using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Alba;
using JasperFx.Core;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;

namespace Wolverine.Http.Tests.Bugs;

public class Bug_505_required_attribute_not_working
{
    [Fact]
    public async Task try_endpoint_hit()
    {
        var breeder = new Breeder();
        var id = Guid.NewGuid();
        AggregateRepository.Breeders[id] = breeder;

        var builder = WebApplication.CreateBuilder();
        builder.Host.UseWolverine();
        builder.Services.AddSingleton<AggregateRepository>();
        

        var authorizationService = Substitute.For<IAuthorizationService>();
        builder.Services.AddSingleton<IAuthorizationService>(authorizationService);
        var principal = new ClaimsPrincipal();

        authorizationService.AuthorizeAsync(principal, id, "EditBreeder").Returns(AuthorizationResult.Success());

        using var host = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });

        // Hit should be 204, no body
        await host.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = principal);
            x.Post.Json(new ChangeVisionCommand("good", id)).ToUrl("/api/breeder/change-vision");
            x.StatusCodeShouldBe(204);
        });
        
        // Miss should 404
        await host.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = principal);
            var breederId = Guid.NewGuid();
            authorizationService.AuthorizeAsync(principal, breederId, "EditBreeder")
                .Returns(AuthorizationResult.Success());
            
            x.Post.Json(new ChangeVisionCommand("good", breederId)).ToUrl("/api/breeder/change-vision");
            x.StatusCodeShouldBe(404);
        });
    }
}

public class Breeder
{
    
}

public class AggregateRepository
{
    public static Dictionary<Guid, Breeder> Breeders { get; } = new();

    public Task<T> LoadAsync<T>(Guid breederId, string what, CancellationToken cancellationToken) where T : class
    {
        if (Breeders.TryGetValue(breederId, out var breeder))
        {
            return Task.FromResult<T>(breeder as T);
        }

        return Task.FromResult<T>(null);
    }
}

public record ChangeVisionCommand(string Vision, Guid BreederId);

public class ChangeBreederVisionEndpoint
{
    public static async Task<IResult> Before(ChangeVisionCommand c, ClaimsPrincipal u, IAuthorizationService auth, CancellationToken ct)
    {
        var authenticated = await auth.AuthorizeAsync(u, c.BreederId, "EditBreeder");
        return !authenticated.Succeeded ? Results.Forbid() : WolverineContinue.Result();
    }

    public static async Task<Breeder?> LoadAsync(ChangeVisionCommand c, AggregateRepository r, CancellationToken ct)
        => await r.LoadAsync<Breeder>(c.BreederId, null, ct);

    [Tags("Breeder")]
    [ProducesResponseType(204)]
    [WolverinePost("/api/breeder/change-vision")]
    public static  async Task<IResult> Post(ChangeVisionCommand c,[Required] Breeder? breeder, AggregateRepository repo, CancellationToken ct)
    {
        if (breeder is null) return Results.NotFound();

        // Don't care for the sake of the test
        // breeder.ChangeNextPlannedLitter(c.Vision);
        //
        // await repo.StoreAsync(breeder, ct);
        return Results.NoContent();
    }
}