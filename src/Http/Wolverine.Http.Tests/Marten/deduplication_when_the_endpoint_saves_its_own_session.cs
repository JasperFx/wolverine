using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Core;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-5835. An endpoint that commits its own claim and then fails is answered with its own failure, not with
/// GH-5206's 409 for a concurrent repeat.
/// </summary>
public class deduplication_when_the_endpoint_saves_its_own_session : IAsyncLifetime
{
    private const string SchemaName = "http_dedup_own_save";

    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.MessageDeduplicationMode = MessageDeduplicationMode.CompareByHash;
            opts.Durability.DeduplicationWindow = 1.Hours();

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(deduplication_when_the_endpoint_saves_its_own_session).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = SchemaName;
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not an endpoint that saves its own session",
                    type => type != typeof(MartenSavesItsOwnSessionEndpoints) &&
                            type != typeof(MartenValidateSavesItsOwnSessionEndpoint)))));

        await ((IHost)theHost).ResetResourceState();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task the_only_request_with_its_id_that_saves_then_throws_fails_as_itself()
    {
        var key = Guid.NewGuid().ToString();
        var name = "throws-" + key;

        await failingPostAsync("/marten-dedup-own-save/save-then-throw", name, key);

        (await countAsync(name)).ShouldBe(1);
        (await claimCountAsync(key)).ShouldBe(1);

        var retry = await postAsync("/marten-dedup-own-save/save-then-throw", name + "-retry", key);
        retry.Context.Response.StatusCode.ShouldBe(409);
        (await countAsync(name + "-retry")).ShouldBe(0);
    }

    [Fact]
    public async Task a_validate_that_saves_then_refuses_keeps_its_own_400()
    {
        var key = Guid.NewGuid().ToString();
        var name = "validate-" + key;

        var result = await postAsync("/marten-dedup-own-save/validate-saves", name, key);

        result.Context.Response.StatusCode.ShouldBe(400);
        (await result.ReadAsJsonAsync<ProblemDetails>())!.Detail.ShouldBe("Refused after saving");
        (await claimCountAsync(key)).ShouldBe(1);
    }

    // A 500 under the developer exception page, else the throw itself through the test server
    private async Task failingPostAsync(string url, string name, string key)
    {
        try
        {
            var result = await postAsync(url, name, key);
            result.Context.Response.StatusCode.ShouldBeGreaterThanOrEqualTo(500);
        }
        catch (InvalidOperationException e) when (e.Message == MartenSavesItsOwnSessionEndpoints.Failure)
        {
        }
    }

    private Task<IScenarioResult> postAsync(string url, string name, string key)
        => theHost.Scenario(x =>
        {
            x.Post.Json(new SaveItsOwnSession(name)).ToUrl(url);
            x.WithRequestHeader("Idempotency-Key", key);
            x.IgnoreStatusCode();
        });

    private static Task<long> claimCountAsync(string key)
        => scalarAsync($"select count(*) from {SchemaName}.wolverine_deduplication_hashed where deduplication_id = @id",
            key);

    private static Task<long> countAsync(string name)
        => scalarAsync($"select count(*) from {SchemaName}.mt_doc_ownsavedocument where data ->> 'Name' = @id", name);

    private static async Task<long> scalarAsync(string sql, string id)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);

        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}

public record SaveItsOwnSession(string Name);

public class OwnSaveDocument
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public static class MartenSavesItsOwnSessionEndpoints
{
    public const string Failure = "Failed after saving its own session";

    [Deduplicated]
    [WolverinePost("/marten-dedup-own-save/save-then-throw")]
    public static async Task<string> SaveThenThrow(SaveItsOwnSession request, IDocumentSession session)
    {
        session.Store(new OwnSaveDocument { Id = Guid.NewGuid(), Name = request.Name });
        await session.SaveChangesAsync();
        throw new InvalidOperationException(Failure);
    }
}

public static class MartenValidateSavesItsOwnSessionEndpoint
{
    public static async Task<ProblemDetails> ValidateAsync(SaveItsOwnSession request, IDocumentSession session)
    {
        session.Store(new OwnSaveDocument { Id = Guid.NewGuid(), Name = request.Name });
        await session.SaveChangesAsync();
        return new ProblemDetails { Detail = "Refused after saving", Status = 400 };
    }

    [Deduplicated]
    [WolverinePost("/marten-dedup-own-save/validate-saves")]
    public static string Post(SaveItsOwnSession request, IDocumentSession session) => "ok";
}
