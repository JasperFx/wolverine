using Alba;
using IntegrationTests;
using Marten;
using Marten.Events.Projections;
using Microsoft.AspNetCore.Builder;
using Shouldly;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace Wolverine.Http.Tests.Marten;

public class reacting_to_read_aggregate : IAsyncLifetime
{
    private IAlbaHost theHost;
    
    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        // config
        builder.Services.AddMarten(opts =>
        {
            // Establish the connection string to your Marten database
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "letter_aggregate";
            opts.Projections.Snapshot<LetterAggregate>(SnapshotLifecycle.Inline);
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Host.UseWolverine(opts => opts.Discovery.IncludeAssembly(GetType().Assembly));

        builder.Services.AddWolverineHttp();

        // This is using Alba, which uses WebApplicationFactory under the covers
        theHost = await AlbaHost.For(builder, app =>
        {
            app.MapWolverineEndpoints();
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (theHost != null)
        {
            await theHost.StopAsync();
        }
    }

    [Fact]
    public async Task get_404_by_default_on_missing()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url("/letters1/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task not_required_still_functions()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/letters2/" + Guid.NewGuid());
        });
        
        result.ReadAsText().ShouldBe("No Letters");
    }

    [Fact]
    public async Task missing_with_problem_details()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Get.Url("/letters3/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }
    
    [Fact]
    public async Task post_write_404_by_default_on_missing_on_write()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Url("/letters4/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task not_required_still_functions_on_write()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Url("/letters5/" + Guid.NewGuid());
        });
        
        result.ReadAsText().ShouldBe("No Letters");
    }

    [Fact]
    public async Task missing_with_problem_details_on_write()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Url("/letters6/" + Guid.NewGuid());
            x.StatusCodeShouldBe(404);
            x.ContentTypeShouldBe("application/problem+json");
        });
    }
    
}

public static class LetterAggregateEndpoint
{
    #region sample_read_aggregate_fine_grained_validation_control

    // Straight up 404 on missing
    [WolverineGet("/letters1/{id}")]
    public static LetterAggregate GetLetter1([ReadAggregate] LetterAggregate letters) => letters;
    
    // Not required
    [WolverineGet("/letters2/{id}")]
    public static string GetLetter2([ReadAggregate(Required = false)] LetterAggregate letters)
    {
        return letters == null ? "No Letters" : "Got Letters";
    }
    
    // Straight up 404 & problem details on missing
    [WolverineGet("/letters3/{id}")]
    public static LetterAggregate GetLetter3([ReadAggregate(OnMissing = OnMissing.ProblemDetailsWith404)] LetterAggregate letters) => letters;

    

    #endregion
    // Straight up 404 on missing
    [WolverinePost("/letters4/{id}")]
    public static LetterAggregate PostLetter4([WriteAggregate(Required = true)] LetterAggregate letters) => letters;
    
    // Not required
    [WolverinePost("/letters5/{id}")]
    public static string PostLetter5([WriteAggregate(Required = false)] LetterAggregate letters)
    {
        return letters == null ? "No Letters" : "Got Letters";
    }
    
    // Straight up 404 & problem details on missing
    [WolverinePost("/letters6/{id}")]
    public static LetterAggregate PostLetter6([WriteAggregate(Required = true, OnMissing = OnMissing.ProblemDetailsWith404)] LetterAggregate letters) => letters;

}

public class LetterStarted;

public class LetterAggregate
{
    public LetterAggregate()
    {
    }

    public LetterAggregate(LetterStarted started)
    {
    }

    public Guid Id { get; set; }
    public int ACount { get; set; }
    public int BCount { get; set; }
    public int CCount { get; set; }
    public int DCount { get; set; }

    public void Apply(AEvent e)
    {
        ACount++;
    }

    public void Apply(BEvent e)
    {
        BCount++;
    }

    public void Apply(CEvent e)
    {
        CCount++;
    }

    public void Apply(DEvent e)
    {
        DCount++;
    }
}

public record AEvent;

public record BEvent;

public record CEvent;

public record DEvent;
