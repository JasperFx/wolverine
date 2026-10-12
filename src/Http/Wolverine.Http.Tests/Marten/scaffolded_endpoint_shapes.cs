using Alba;
using IntegrationTests;
using JasperFx.Events;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;
using Xunit;

namespace Wolverine.Http.Tests.Marten.ScaffoldedEndpointShapes;

public class scaffolded_endpoint_shapes : IAsyncLifetime
{
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(scaffolded_endpoint_shapes).Assembly);
        });

        builder.Services.AddMarten(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "scaffolded_endpoint_shapes";
            m.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddWolverineHttp();

        // Set up, never reset: a reset clears resources other hosts in this test run share
        builder.Services.AddResourceSetupOnStartup();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
            {
                // The shapes are [WolverineIgnore]d so no other host scanning this assembly, most of them
                // without an event store, tries to compile them; a user include brings them back here
                q.Includes.WithCondition("A scaffolded endpoint shape", type => Endpoints.Contains(type));
                q.Excludes.WithCondition("Not a scaffolded endpoint shape", type => !Endpoints.Contains(type));
            })));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    private static readonly Type[] Endpoints =
    [
        typeof(OpenScaffoldLedgerEndpoint), typeof(NoteScaffoldLedgerEndpoint), typeof(NoteScaffoldBothEndpoint),
        typeof(GetScaffoldLedgerEndpoint), typeof(GetScaffoldLedgerCardEndpoint),
        typeof(OpenScaffoldTicketEndpoint), typeof(CloseScaffoldTicketEndpoint), typeof(GetScaffoldTicketEndpoint)
    ];

    [Fact]
    public async Task a_stream_start_answers_201_with_the_id_it_assigned()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new OpenScaffoldLedger("petty cash")).ToUrl("/api/scaffold-shapes/open-scaffold-ledger");
            x.StatusCodeShouldBe(201);
        });

        var created = await result.ReadAsJsonAsync<CreationResponse<Guid>>();
        created!.Value.ShouldNotBe(Guid.Empty);
        result.Context.Response.Headers.Location.ToString().ShouldBe($"/api/scaffold-ledger/{created.Value}");

        (await eventsOn(created.Value)).Single().ShouldBeOfType<ScaffoldLedgerOpened>().Name.ShouldBe("petty cash");
    }

    [Fact]
    public async Task one_event_onto_the_stream_it_loads_and_validate_refuses_with_a_400()
    {
        var ledger = await open();

        await theHost.Scenario(x =>
        {
            x.Post.Json(new NoteScaffoldLedger(ledger, "receipts")).ToUrl("/api/scaffold-shapes/note-scaffold-ledger");
            x.StatusCodeShouldBe(204);
        });

        await theHost.Scenario(x =>
        {
            x.Post.Json(new NoteScaffoldLedger(ledger, "")).ToUrl("/api/scaffold-shapes/note-scaffold-ledger");
            x.StatusCodeShouldBe(400);
        });

        (await eventsOn(ledger)).Select(x => x.GetType()).ShouldBe([typeof(ScaffoldLedgerOpened), typeof(ScaffoldLedgerNoted)]);
    }

    [Fact]
    public async Task a_stream_that_does_not_exist_is_a_404()
    {
        await theHost.Scenario(x =>
        {
            x.Post.Json(new NoteScaffoldLedger(Guid.NewGuid(), "receipts")).ToUrl("/api/scaffold-shapes/note-scaffold-ledger");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_command_deciding_against_two_streams_appends_to_each()
    {
        var ledger = await open();
        var journal = Guid.CreateVersion7();
        await startJournal(journal);

        await theHost.Scenario(x =>
        {
            x.Post.Json(new NoteScaffoldBoth(ledger, journal, "audit")).ToUrl("/api/scaffold-shapes/note-scaffold-both");
            x.StatusCodeShouldBe(204);
        });

        (await eventsOn(ledger)).Last().ShouldBeOfType<ScaffoldLedgerNoted>();
        (await eventsOn(journal)).Last().ShouldBeOfType<ScaffoldJournalNoted>();
    }

    [Fact]
    public async Task a_query_follows_the_location_of_a_stream_start_to_the_aggregate()
    {
        // GH-4975: the scaffolded GET sits at the route a scaffolded stream start's Location points at
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new OpenScaffoldLedger("petty cash")).ToUrl("/api/scaffold-shapes/open-scaffold-ledger");
            x.StatusCodeShouldBe(201);
        });
        var location = result.Context.Response.Headers.Location.ToString();
        var id = (await result.ReadAsJsonAsync<CreationResponse<Guid>>())!.Value;

        await theHost.Scenario(x =>
        {
            x.Post.Json(new NoteScaffoldLedger(id, "receipts")).ToUrl("/api/scaffold-shapes/note-scaffold-ledger");
            x.StatusCodeShouldBe(204);
        });

        var ledger = await theHost.GetAsJson<ScaffoldLedger>(location);
        ledger!.Notes.ShouldBe(1);
    }

    [Fact]
    public async Task a_query_of_something_that_does_not_exist_is_a_404()
    {
        await theHost.Scenario(x =>
        {
            x.Get.Url($"/api/scaffold-ledger/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_query_of_a_document_loads_it_by_the_route_parameter_that_names_it()
    {
        var card = new ScaffoldLedgerCard { Id = Guid.CreateVersion7(), Title = "petty cash" };
        await using (var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession())
        {
            session.Store(card);
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var loaded = await theHost.GetAsJson<ScaffoldLedgerCard>($"/api/scaffold-cards/{card.Id}");
        loaded!.Title.ShouldBe("petty cash");
    }

    [Fact]
    public async Task strong_typed_ids_run_through_a_stream_start_a_command_and_a_query()
    {
        // GH-4883: --strong-typed-ids -- a plain record struct, which Marten stores as the Guid it wraps
        var started = await theHost.Scenario(x =>
        {
            x.Post.Json(new OpenScaffoldTicket("printer jam")).ToUrl("/api/scaffold-shapes/open-scaffold-ticket");
            x.StatusCodeShouldBe(201);
        });
        var id = (await started.ReadAsJsonAsync<CreationResponse<ScaffoldTicketId>>())!.Value;
        id.Value.ShouldNotBe(Guid.Empty);

        var location = started.Context.Response.Headers.Location.ToString();
        location.ShouldBe($"/api/scaffold-ticket/{id.Value}");

        await theHost.Scenario(x =>
        {
            x.Post.Json(new CloseScaffoldTicket(id)).ToUrl("/api/scaffold-shapes/close-scaffold-ticket");
            x.StatusCodeShouldBe(204);
        });

        var ticket = await theHost.GetAsJson<ScaffoldTicket>(location);
        ticket!.Id.ShouldBe(id);
        ticket.Closed.ShouldBeTrue();

        (await eventsOn(id.Value)).Select(x => x.GetType()).ShouldBe([typeof(ScaffoldTicketOpened), typeof(ScaffoldTicketClosed)]);
    }

    private async Task<Guid> open()
    {
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new OpenScaffoldLedger("petty cash")).ToUrl("/api/scaffold-shapes/open-scaffold-ledger");
            x.StatusCodeShouldBe(201);
        });

        return (await result.ReadAsJsonAsync<CreationResponse<Guid>>())!.Value;
    }

    private async Task<IReadOnlyList<object>> eventsOn(Guid id)
    {
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().QuerySession();
        return (await session.Events.FetchStreamAsync(id, token: TestContext.Current.CancellationToken)).Select(x => x.Data).ToList();
    }

    private async Task startJournal(Guid id)
    {
        await using var session = theHost.Services.GetRequiredService<IDocumentStore>().LightweightSession();
        session.Events.StartStream<ScaffoldJournal>(id, new ScaffoldJournalOpened());
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);
    }
}

// GH-4927: the HTTP endpoint shapes `wolverine scaffold` writes, filled in and run against a real host -- the
// HTTP twin of scaffolded_handler_shapes. Each class is exactly what the scaffold writes for its slice, with
// the TODO body replaced by the shape its comment gives, plus the [WolverineIgnore] that keeps them out of
// every other host in this assembly.

public record OpenScaffoldLedger(string Name);
public record NoteScaffoldLedger(Guid Id, string Note);
public record NoteScaffoldBoth(Guid ScaffoldLedgerId, Guid ScaffoldJournalId, string Note);

public record ScaffoldLedgerOpened(string Name);
public record ScaffoldLedgerNoted(string Note);
public record ScaffoldJournalOpened;
public record ScaffoldJournalNoted(string Note);

public class ScaffoldLedger
{
    public Guid Id { get; set; }
    public int Notes { get; set; }
    public void Apply(ScaffoldLedgerOpened e) { }
    public void Apply(ScaffoldLedgerNoted e) => Notes++;
}

public class ScaffoldJournal
{
    public Guid Id { get; set; }
    public int Notes { get; set; }
    public void Apply(ScaffoldJournalOpened e) { }
    public void Apply(ScaffoldJournalNoted e) => Notes++;
}

// A stream start: a 201 with the id the endpoint assigned (GH-4927)
[WolverineIgnore]
public static class OpenScaffoldLedgerEndpoint
{
    [WolverinePost("/api/scaffold-shapes/open-scaffold-ledger")]
    public static (CreationResponse<Guid>, StartStream) Post(OpenScaffoldLedger command)
    {
        var id = Guid.CreateVersion7();
        return (new CreationResponse<Guid>($"/api/scaffold-ledger/{id}", id),
            Storage.StartStream<ScaffoldLedger>(id, new ScaffoldLedgerOpened(command.Name)));
    }
}

// One event onto the stream it loads, guarded by Validate (GH-4889)
[WolverineIgnore]
public static class NoteScaffoldLedgerEndpoint
{
    public static ProblemDetails Validate(NoteScaffoldLedger command, ScaffoldLedger scaffoldLedger)
        => string.IsNullOrEmpty(command.Note)
            ? new ProblemDetails { Detail = "A note needs some text", Status = 400 }
            : WolverineContinue.NoProblems;

    [WolverinePost("/api/scaffold-shapes/note-scaffold-ledger")]
    [EmptyResponse]
    public static ScaffoldLedgerNoted? Post(NoteScaffoldLedger command, [WriteModel] ScaffoldLedger scaffoldLedger)
        => new(command.Note);
}

// A command deciding against two streams: one IEventStream<T> each (GH-4895)
[WolverineIgnore]
public static class NoteScaffoldBothEndpoint
{
    public static ProblemDetails Validate(NoteScaffoldBoth command) => WolverineContinue.NoProblems;

    [WolverinePost("/api/scaffold-shapes/note-scaffold-both")]
    [EmptyResponse]
    public static void Post(
        NoteScaffoldBoth command,
        [WriteModel(nameof(NoteScaffoldBoth.ScaffoldLedgerId))] IEventStream<ScaffoldLedger> scaffoldLedgerStream,
        [WriteModel(nameof(NoteScaffoldBoth.ScaffoldJournalId))] IEventStream<ScaffoldJournal> scaffoldJournalStream)
    {
        scaffoldLedgerStream.AppendOne(new ScaffoldLedgerNoted(command.Note));
        scaffoldJournalStream.AppendOne(new ScaffoldJournalNoted(command.Note));
    }
}

// GH-4975: a Query over HTTP -- a GET reading an aggregate by its events ([ReadModel], no lock), at the route a
// scaffolded stream start's Location points at
[WolverineIgnore]
public static class GetScaffoldLedgerEndpoint
{
    [WolverineGet("/api/scaffold-ledger/{id}")]
    public static ScaffoldLedger Get([ReadModel] ScaffoldLedger scaffoldLedger)
    {
        return scaffoldLedger;
    }
}

public class ScaffoldLedgerCard
{
    public Guid Id { get; set; }
    public string Title { get; set; } = "";
}

// ...and a GET reading a document ([Entity]) by the route parameter of the slice's declared route
[WolverineIgnore]
public static class GetScaffoldLedgerCardEndpoint
{
    [WolverineGet("/api/scaffold-cards/{cardId}", RouteName = "ScaffoldCard")]
    public static ScaffoldLedgerCard Get([Entity("cardId")] ScaffoldLedgerCard scaffoldLedgerCard)
    {
        return scaffoldLedgerCard;
    }
}

// GH-4883: what --strong-typed-ids writes for the identity, the aggregate, its messages and its endpoints
public readonly record struct ScaffoldTicketId(Guid Value)
{
    // Wolverine.Http binds a route or query string value to it through this TryParse
    public static bool TryParse(string? text, IFormatProvider? provider, out ScaffoldTicketId id)
    {
        var parsed = Guid.TryParse(text, provider, out var value);
        id = new ScaffoldTicketId(value);
        return parsed;
    }
}

public record OpenScaffoldTicket(string Title);
public record CloseScaffoldTicket(ScaffoldTicketId ScaffoldTicketId);
public record ScaffoldTicketOpened(ScaffoldTicketId ScaffoldTicketId);
public record ScaffoldTicketClosed(ScaffoldTicketId ScaffoldTicketId);

public class ScaffoldTicket
{
    public ScaffoldTicketId Id { get; set; }
    public bool Closed { get; set; }
    public void Apply(ScaffoldTicketOpened e) { }
    public void Apply(ScaffoldTicketClosed e) => Closed = true;
}

[WolverineIgnore]
public static class OpenScaffoldTicketEndpoint
{
    [WolverinePost("/api/scaffold-shapes/open-scaffold-ticket")]
    public static (CreationResponse<ScaffoldTicketId>, StartStream) Post(OpenScaffoldTicket command)
    {
        var id = new ScaffoldTicketId(Guid.CreateVersion7());
        return (new CreationResponse<ScaffoldTicketId>($"/api/scaffold-ticket/{id.Value}", id),
            Storage.StartStream<ScaffoldTicket>(id.Value, new ScaffoldTicketOpened(id)));
    }
}

[WolverineIgnore]
public static class CloseScaffoldTicketEndpoint
{
    public static ProblemDetails Validate(CloseScaffoldTicket command, ScaffoldTicket scaffoldTicket)
        => WolverineContinue.NoProblems;

    [WolverinePost("/api/scaffold-shapes/close-scaffold-ticket")]
    [EmptyResponse]
    public static ScaffoldTicketClosed? Post(CloseScaffoldTicket command, [WriteModel] ScaffoldTicket scaffoldTicket)
        => new(command.ScaffoldTicketId);
}

[WolverineIgnore]
public static class GetScaffoldTicketEndpoint
{
    [WolverineGet("/api/scaffold-ticket/{id}")]
    public static ScaffoldTicket Get([ReadModel] ScaffoldTicket scaffoldTicket)
    {
        return scaffoldTicket;
    }
}
