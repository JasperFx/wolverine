using System.Security.Claims;
using Alba;
using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Marten;
using Xunit;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
/// GH-4741. With EnableRelayOfUserName the user reaches the outboxed Marten session on every HTTP chain
/// that opens one, not only those taking IMessageBus or IMessageContext.
/// </summary>
public class user_name_relay_into_the_marten_session : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await UserNameRelayHost.StartAsync(relayUserName: true, "http_user_relay");
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
            x.ConfigureHttpContext(c => c.User = UserNameRelayHost.Principal("han@rebellion.org"));
            x.Post.Json(new RelayedNoteRequest(id, "side effect only")).ToUrl("/user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        (await UserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBe("han@rebellion.org");
    }

    [Fact]
    public async Task an_event_appended_only_through_a_side_effect_records_the_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = UserNameRelayHost.Principal("leia@rebellion.org"));
            x.Post.Json(new StartRelayedJourney(id, "Alderaan")).ToUrl("/user-relay/journey");
            x.StatusCodeShouldBe(204);
        });

        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var events = await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken);

        events.Single().UserName.ShouldBe("leia@rebellion.org");
    }

    [Fact]
    public async Task an_endpoint_that_takes_the_message_bus_records_the_user_too()
    {
        // Takes IMessageBus.
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.ConfigureHttpContext(c => c.User = UserNameRelayHost.Principal("chewie@rebellion.org"));
            x.Post.Json(new RelayedNoteRequest(id, "with a bus")).ToUrl("/user-relay/note-with-bus");
            x.StatusCodeShouldBe(204);
        });

        (await UserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBe("chewie@rebellion.org");
    }

    [Fact]
    public async Task an_anonymous_request_records_no_user()
    {
        var id = Guid.NewGuid();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new RelayedNoteRequest(id, "nobody")).ToUrl("/user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        (await UserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBeNull();
    }

    [Fact]
    public async Task the_user_is_on_the_message_context_before_the_session_is_opened()
    {
        // OpenSession() reads UserName immediately, so the relay must precede it.
        await theHost.Scenario(x =>
        {
            x.Post.Json(new RelayedNoteRequest(Guid.NewGuid(), "warmup")).ToUrl("/user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == "/user-relay/note");

        var source = chain.SourceCode.ShouldNotBeNull();

        var relay = source.IndexOf("UserNameMiddleware.Apply", StringComparison.Ordinal);
        var open = source.IndexOf(".OpenSession(", StringComparison.Ordinal);

        relay.ShouldBeGreaterThan(-1, source);
        open.ShouldBeGreaterThan(-1, source);
        relay.ShouldBeLessThan(open, source);
    }
}

/// <summary>
/// GH-4741. With the option off, nothing reaches the session.
/// </summary>
public class user_name_relay_into_the_marten_session_when_disabled : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await UserNameRelayHost.StartAsync(relayUserName: false, "http_user_relay_off");
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
            x.ConfigureHttpContext(c => c.User = UserNameRelayHost.Principal("han@rebellion.org"));
            x.Post.Json(new RelayedNoteRequest(id, "relay off")).ToUrl("/user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        (await UserNameRelayHost.LastModifiedByAsync(theHost, id)).ShouldBeNull();
    }

    [Fact]
    public async Task and_the_generated_endpoint_does_not_relay_at_all()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new RelayedNoteRequest(Guid.NewGuid(), "warmup")).ToUrl("/user-relay/note");
            x.StatusCodeShouldBe(204);
        });

        var chain = theHost.Services.GetRequiredService<WolverineHttpOptions>().Endpoints!.Chains
            .Single(x => x.RoutePattern!.RawText == "/user-relay/note");

        chain.SourceCode.ShouldNotBeNull().ShouldNotContain("UserNameMiddleware");
    }
}

internal static class UserNameRelayHost
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
            opts.Discovery.IncludeAssembly(typeof(UserNameRelayHost).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = schemaName;
            m.DisableNpgsqlLogging = true;

            // Opt-in columns; without them the assertions are vacuous.
            m.Schema.For<RelayedNote>().Metadata(x => x.LastModifiedBy.Enabled = true);
            m.Events.MetadataConfig.UserNameEnabled = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        // Only this class's endpoints.
        var host = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a user name relay test endpoint",
                    type => type != typeof(UserNameRelayEndpoints)))));

        await ((IHost)host).ResetResourceState();

        return host;
    }

    public static ClaimsPrincipal Principal(string name)
    {
        return new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, name)], "TestAuth"));
    }

    public static async Task<string?> LastModifiedByAsync(IAlbaHost host, Guid id)
    {
        await using var session = host.Services.GetRequiredService<IDocumentStore>().QuerySession();
        var note = await session.LoadAsync<RelayedNote>(id, TestContext.Current.CancellationToken);
        note.ShouldNotBeNull();

        var metadata = await session.MetadataForAsync(note, TestContext.Current.CancellationToken);
        return metadata!.LastModifiedBy;
    }
}

public record RelayedNoteRequest(Guid Id, string Text);

public record StartRelayedJourney(Guid Id, string Destination);

public record RelayedJourneyStarted(string Destination);

public class RelayedNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class RelayedJourney
{
    public Guid Id { get; set; }
    public string Destination { get; set; } = string.Empty;

    public void Apply(RelayedJourneyStarted e) => Destination = e.Destination;
}

public static class UserNameRelayEndpoints
{
    // No IMessageBus or IMessageContext: only the side effect opens the session.
    [WolverinePost("/user-relay/note")]
    public static IMartenOp StoreNote(RelayedNoteRequest request)
        => MartenOps.Store(new RelayedNote { Id = request.Id, Text = request.Text });

    [WolverinePost("/user-relay/journey")]
    public static IStartStream StartJourney(StartRelayedJourney request)
        => MartenOps.StartStream<RelayedJourney>(request.Id, new RelayedJourneyStarted(request.Destination));

    [WolverinePost("/user-relay/note-with-bus")]
    public static void StoreNoteWithBus(RelayedNoteRequest request, IDocumentSession session, IMessageBus bus)
        => session.Store(new RelayedNote { Id = request.Id, Text = request.Text });
}
