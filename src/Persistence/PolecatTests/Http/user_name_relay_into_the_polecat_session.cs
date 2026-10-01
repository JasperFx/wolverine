using System.Security.Claims;
using Alba;
using IntegrationTests;
using JasperFx.Resources;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Polecat.Attributes;
using Shouldly;
using Wolverine;
using Wolverine.Http;
using Wolverine.Polecat;

namespace PolecatTests.Http;

/// <summary>
/// GH-4741. Polecat mirror of user_name_relay_into_the_marten_session (LastModifiedBy).
/// </summary>
public class user_name_relay_into_the_polecat_session : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await PolecatUserNameRelayHost.StartAsync(relayUserName: true, "http_user_relay");
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task a_document_written_only_through_a_side_effect_records_the_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = PolecatUserNameRelayHost.Principal("han@rebellion.org"));
            x.Post.Json(new PolecatRelayedNoteRequest(id, "side effect only")).ToUrl("/polecat-user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        (await PolecatUserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBe("han@rebellion.org");
    }

    [Fact]
    public async Task an_event_appended_only_through_a_side_effect_records_the_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = PolecatUserNameRelayHost.Principal("leia@rebellion.org"));
            x.Post.Json(new StartPolecatRelayedJourney(id, "Alderaan")).ToUrl("/polecat-user-relay/journey");
            x.StatusCodeShouldBe(204);
        });

        await using var session = ((IHost)theHost).DocumentStore().QuerySession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Single().UserName.ShouldBe("leia@rebellion.org");
    }

    [Fact]
    public async Task an_endpoint_that_takes_the_message_bus_records_the_user_too()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = PolecatUserNameRelayHost.Principal("chewie@rebellion.org"));
            x.Post.Json(new PolecatRelayedNoteRequest(id, "with a bus")).ToUrl("/polecat-user-relay/note-with-bus");
            x.StatusCodeShouldBe(204);
        });

        (await PolecatUserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBe("chewie@rebellion.org");
    }
}

/// <summary>
/// GH-4741. With the option off, nothing reaches the session.
/// </summary>
public class user_name_relay_into_the_polecat_session_when_disabled : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await PolecatUserNameRelayHost.StartAsync(relayUserName: false, "http_user_relay_off");
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    [Fact]
    public async Task the_session_records_no_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = PolecatUserNameRelayHost.Principal("han@rebellion.org"));
            x.Post.Json(new PolecatRelayedNoteRequest(id, "relay off")).ToUrl("/polecat-user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        (await PolecatUserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBeNull();
    }
}

internal static class PolecatUserNameRelayHost
{
    public static async Task<IAlbaHost> StartAsync(bool relayUserName, string schemaName)
    {
        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.EnableRelayOfUserName = relayUserName;

            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();

            // The application assembly is cached process-wide; include this one explicitly.
            opts.Discovery.IncludeAssembly(typeof(PolecatUserNameRelayHost).Assembly);
        });

        builder.Services.AddPolecat(m =>
        {
            m.ConnectionString = Servers.SqlServerConnectionString;
            m.DatabaseSchemaName = schemaName;

            // Opt-in column; without it the event assertion is vacuous.
            m.Events.EnableUserName = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a user name relay test endpoint",
                    type => type != typeof(PolecatUserNameRelayEndpoints)))));

        await ((IHost)host).ResetResourceState();

        return host;
    }

    public static ClaimsPrincipal Principal(string name)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "TestAuth"));
    }

    public static async Task<string?> LastModifiedByAsync(IAlbaHost host, Guid id)
    {
        await using var session = ((IHost)host).DocumentStore().QuerySession();
        var note = await session.LoadAsync<PolecatRelayedNote>(id, TestContext.Current.CancellationToken);

        return note.ShouldNotBeNull().LastModifiedBy;
    }
}

public record PolecatRelayedNoteRequest(Guid Id, string Text);

public record StartPolecatRelayedJourney(Guid Id, string Destination);

public record PolecatRelayedJourneyStarted(string Destination);

public class PolecatRelayedNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;

    [LastModifiedByMetadata]
    public string? LastModifiedBy { get; set; }
}

public class PolecatRelayedJourney
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty;

    public void Apply(PolecatRelayedJourneyStarted e) => Destination = e.Destination;
}

public static class PolecatUserNameRelayEndpoints
{
    // No IMessageBus or IMessageContext: only the side effect opens the session.
    [WolverinePost("/polecat-user-relay/note")]
    public static IPolecatOp StoreNote(PolecatRelayedNoteRequest request)
        => PolecatOps.Store(new PolecatRelayedNote { Id = request.Id, Text = request.Text });

    [WolverinePost("/polecat-user-relay/journey")]
    public static IStartStream StartJourney(StartPolecatRelayedJourney request)
        => PolecatOps.StartStream<PolecatRelayedJourney>(request.Id,
            new PolecatRelayedJourneyStarted(request.Destination));

    [WolverinePost("/polecat-user-relay/note-with-bus")]
    public static void StoreNoteWithBus(PolecatRelayedNoteRequest request, IDocumentSession session, IMessageBus bus)
        => session.Store(new PolecatRelayedNote { Id = request.Id, Text = request.Text });
}
