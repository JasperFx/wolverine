using System.Security.Claims;
using Alba;
using Fisher;
using Fisher.Attributes;
using JasperFx;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Http;

namespace FisherTests.Http;

/// <summary>
/// GH-4741. Fisher mirror of user_name_relay_into_the_marten_session (CurrentUserName).
/// </summary>
public class user_name_relay_into_the_fisher_session : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("user_relay_http");
        theHost = await FisherUserNameRelayHost.StartAsync(relayUserName: true, theDatabase);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task a_document_written_only_through_a_side_effect_records_the_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = FisherUserNameRelayHost.Principal("han@rebellion.org"));
            x.Post.Json(new FisherRelayedNoteRequest(id, "side effect only")).ToUrl("/fisher-user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        (await FisherUserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBe("han@rebellion.org");
    }

    [Fact]
    public async Task an_event_appended_only_through_a_side_effect_records_the_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = FisherUserNameRelayHost.Principal("leia@rebellion.org"));
            x.Post.Json(new StartFisherRelayedJourney(id, "Alderaan")).ToUrl("/fisher-user-relay/journey");
            x.StatusCodeShouldBe(204);
        });

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Single().UserName.ShouldBe("leia@rebellion.org");
    }

    [Fact]
    public async Task an_endpoint_that_takes_the_message_bus_records_the_user_too()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = FisherUserNameRelayHost.Principal("chewie@rebellion.org"));
            x.Post.Json(new FisherRelayedNoteRequest(id, "with a bus")).ToUrl("/fisher-user-relay/note-with-bus");
            x.StatusCodeShouldBe(204);
        });

        (await FisherUserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBe("chewie@rebellion.org");
    }
}

/// <summary>
/// GH-4741. With the option off, nothing reaches the session.
/// </summary>
public class user_name_relay_into_the_fisher_session_when_disabled : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("user_relay_http_off");
        theHost = await FisherUserNameRelayHost.StartAsync(relayUserName: false, theDatabase);
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
        theDatabase.Dispose();
    }

    [Fact]
    public async Task the_session_records_no_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = FisherUserNameRelayHost.Principal("han@rebellion.org"));
            x.Post.Json(new FisherRelayedNoteRequest(id, "relay off")).ToUrl("/fisher-user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        (await FisherUserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBeNull();
    }
}

internal static class FisherUserNameRelayHost
{
    public static async Task<IAlbaHost> StartAsync(bool relayUserName, FisherTestDatabase database)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.EnableRelayOfUserName = relayUserName;

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();

            // The application assembly is cached process-wide; include this one explicitly.
            opts.Discovery.IncludeAssembly(typeof(FisherUserNameRelayHost).Assembly);
        });

        builder.Services.AddFisher(m =>
            {
                m.Connection(database.ConnectionString);
                m.AutoCreateSchemaObjects = AutoCreate.All;

                // Opt-in column; without it the event assertion is vacuous.
                m.Events.EnableUserName = true;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

        builder.Services.AddWolverineHttp();

        return await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a user name relay test endpoint",
                    type => type != typeof(FisherUserNameRelayEndpoints)))));
    }

    public static ClaimsPrincipal Principal(string name)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "TestAuth"));
    }

    public static async Task<string?> LastModifiedByAsync(IAlbaHost host, Guid id)
    {
        await using var session = host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var note = await session.LoadAsync<FisherRelayedNote>(id, TestContext.Current.CancellationToken);

        return note.ShouldNotBeNull().LastModifiedBy;
    }
}

public record FisherRelayedNoteRequest(Guid Id, string Text);

public record StartFisherRelayedJourney(Guid Id, string Destination);

public record FisherRelayedJourneyStarted(string Destination);

public class FisherRelayedNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;

    [LastModifiedByMetadata]
    public string? LastModifiedBy { get; set; }
}

public class FisherRelayedJourney
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty;

    public void Apply(FisherRelayedJourneyStarted e) => Destination = e.Destination;
}

public static class FisherUserNameRelayEndpoints
{
    // No IMessageBus or IMessageContext: only the side effect opens the session.
    [WolverinePost("/fisher-user-relay/note")]
    public static IFisherOp StoreNote(FisherRelayedNoteRequest request)
        => FisherOps.Store(new FisherRelayedNote { Id = request.Id, Text = request.Text });

    [WolverinePost("/fisher-user-relay/journey")]
    public static IStartStream StartJourney(StartFisherRelayedJourney request)
        => FisherOps.StartStream<FisherRelayedJourney>(request.Id,
            new FisherRelayedJourneyStarted(request.Destination));

    [WolverinePost("/fisher-user-relay/note-with-bus")]
    public static void StoreNoteWithBus(FisherRelayedNoteRequest request, IDocumentSession session, IMessageBus bus)
        => session.Store(new FisherRelayedNote { Id = request.Id, Text = request.Text });
}
