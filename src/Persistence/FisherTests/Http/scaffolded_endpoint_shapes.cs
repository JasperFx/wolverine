using System.Text.Json.Serialization;
using System.Text.Json;
using System.Diagnostics.CodeAnalysis;
using Alba;
using Fisher;
using JasperFx;
using JasperFx.Events;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Fisher;
using Wolverine.Http;
using Wolverine.Persistence;
using Wolverine.Persistence.EventSourcing;

namespace FisherTests.Http.ScaffoldedEndpointShapes;

public class scaffolded_endpoint_shapes : IAsyncLifetime
{
    private FisherTestDatabase theDatabase = null!;
    private IAlbaHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase(nameof(scaffolded_endpoint_shapes));

        var builder = WebApplication.CreateBuilder([]);
        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Discovery.DisableConventionalDiscovery();
            opts.Policies.AutoApplyTransactions();
            opts.Discovery.IncludeAssembly(typeof(scaffolded_endpoint_shapes).Assembly);
        });

        builder.Services.AddFisher(m =>
            {
                m.Connection(theDatabase.ConnectionString);
                m.AutoCreateSchemaObjects = AutoCreate.All;
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a scaffolded endpoint shape", type => !Endpoints.Contains(type)))));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
        theDatabase.Dispose();
    }

    private static readonly Type[] Endpoints =
    [
        typeof(OpenScaffoldLedgerEndpoint), typeof(NoteScaffoldLedgerEndpoint), typeof(NoteScaffoldBothEndpoint),
        typeof(GetScaffoldLedgerEndpoint), typeof(OpenScaffoldTicketEndpoint), typeof(CloseScaffoldTicketEndpoint),
        typeof(GetScaffoldTicketEndpoint)
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
    public async Task a_query_reads_the_aggregate_at_the_location_of_its_stream_start()
    {
        // GH-4975: the scaffolded GET sits at the route a scaffolded stream start's Location points at
        var result = await theHost.Scenario(x =>
        {
            x.Post.Json(new OpenScaffoldLedger("petty cash")).ToUrl("/api/scaffold-shapes/open-scaffold-ledger");
            x.StatusCodeShouldBe(201);
        });

        var ledger = await theHost.GetAsJson<ScaffoldLedger>(result.Context.Response.Headers.Location.ToString());
        ledger!.Notes.ShouldBe(0);
    }

    [Fact]
    public async Task strong_typed_ids_run_through_a_stream_start_a_command_and_a_query()
    {
        // GH-4883: --strong-typed-ids writes a plain record struct, which the store keeps as the Guid it wraps
        var started = await theHost.Scenario(x =>
        {
            x.Post.Json(new OpenScaffoldTicket("printer jam")).ToUrl("/api/scaffold-shapes/open-scaffold-ticket");
            x.StatusCodeShouldBe(201);
        });
        var id = (await started.ReadAsJsonAsync<CreationResponse<ScaffoldTicketId>>())!.Value;
        id.Value.ShouldNotBe(Guid.Empty);

        await theHost.Scenario(x =>
        {
            x.Post.Json(new CloseScaffoldTicket(id)).ToUrl("/api/scaffold-shapes/close-scaffold-ticket");
            x.StatusCodeShouldBe(204);
        });

        var ticket = await theHost.GetAsJson<ScaffoldTicket>(started.Context.Response.Headers.Location.ToString());
        ticket!.Id.ShouldBe(id);
        ticket.Closed.ShouldBeTrue();
    }

    [Fact]
    public async Task a_strong_typed_id_is_a_bare_guid_in_every_json_body_and_round_trips_through_the_store()
    {
        // GH-4883: the 201's body carries the bare Guid
        var started = await theHost.Scenario(x =>
        {
            x.Post.Json(new OpenScaffoldTicket("jammed tray")).ToUrl("/api/scaffold-shapes/open-scaffold-ticket");
            x.StatusCodeShouldBe(201);
        });
        var body = await started.ReadAsTextAsync();
        var id = (await started.ReadAsJsonAsync<CreationResponse<ScaffoldTicketId>>())!.Value;
        body.ShouldContain($"\"value\":\"{id.Value}\"");

        // A request body written by hand with the bare Guid binds to the lone Id
        await theHost.Scenario(x =>
        {
            x.Post.Text($"{{\"id\":\"{id.Value}\"}}").ToUrl("/api/scaffold-shapes/close-scaffold-ticket").ContentType("application/json");
            x.StatusCodeShouldBe(204);
        });

        // The response body of the GET carries it bare too, routed by the id's TryParse
        var read = await theHost.Scenario(x => x.Get.Url($"/api/scaffold-ticket/{id}"));
        (await read.ReadAsTextAsync()).ShouldContain($"\"id\":\"{id.Value}\"");

        // An event member round-trips through the store
        var opened = (await eventsOn(id.Value)).OfType<ScaffoldTicketOpened>().Single();
        opened.ScaffoldTicketId.ShouldBe(id);

        // ...and so does a document identified by it
        var store = theHost.Services.GetRequiredService<IDocumentStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(new ScaffoldTicketNote { Id = id, Text = "toner" });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await using (var query = store.QuerySession())
        {
            (await query.LoadAsync<ScaffoldTicketNote>(id, TestContext.Current.CancellationToken))!.Text.ShouldBe("toner");
        }
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
// the TODO body replaced by the shape its comment gives.

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

// GH-4975: a Query over HTTP -- a GET reading an aggregate by its events ([ReadModel], no lock)
public static class GetScaffoldLedgerEndpoint
{
    [WolverineGet("/api/scaffold-ledger/{id}")]
    public static ScaffoldLedger Get([ReadModel] ScaffoldLedger scaffoldLedger)
    {
        return scaffoldLedger;
    }
}

// GH-4883: what --strong-typed-ids writes for the identity, the aggregate, its messages and its endpoints
// GH-4883: exactly what --strong-typed-ids writes for an identity
[JsonConverter(typeof(ScaffoldTicketId.ScaffoldTicketIdJsonConverter))]
public readonly record struct ScaffoldTicketId(Guid Value) : IParsable<ScaffoldTicketId>
{
    public static ScaffoldTicketId Parse(string s, IFormatProvider? provider) => new(Guid.Parse(s, provider));

    // The overload Wolverine.Http's generated code binds a route or query string value with
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out ScaffoldTicketId result)
    {
        var parsed = Guid.TryParse(s, provider, out var value);
        result = new ScaffoldTicketId(value);
        return parsed;
    }

    public override string ToString() => Value.ToString();

    // The bare Guid in System.Text.Json -- bodies, messages, documents, events -- and as a dictionary key
    public sealed class ScaffoldTicketIdJsonConverter : JsonConverter<ScaffoldTicketId>
    {
        public override ScaffoldTicketId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(reader.GetGuid());

        public override void Write(Utf8JsonWriter writer, ScaffoldTicketId value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.Value);

        public override ScaffoldTicketId ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
            => new(Guid.Parse(reader.GetString()!));

        public override void WriteAsPropertyName(Utf8JsonWriter writer, ScaffoldTicketId value, JsonSerializerOptions options)
            => writer.WritePropertyName(value.Value.ToString());
    }
}

// A document identified by the same strong-typed id
public class ScaffoldTicketNote
{
    public ScaffoldTicketId Id { get; set; }
    public string Text { get; set; } = "";
}

public record OpenScaffoldTicket(string Title);
// GH-4883: a stub command names the one stream it identifies Id
public record CloseScaffoldTicket(ScaffoldTicketId Id);
public record ScaffoldTicketOpened(ScaffoldTicketId ScaffoldTicketId);
public record ScaffoldTicketClosed(ScaffoldTicketId ScaffoldTicketId);

public class ScaffoldTicket
{
    public ScaffoldTicketId Id { get; set; }
    public bool Closed { get; set; }
    public void Apply(ScaffoldTicketOpened e) { }
    public void Apply(ScaffoldTicketClosed e) => Closed = true;
}

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

public static class CloseScaffoldTicketEndpoint
{
    public static ProblemDetails Validate(CloseScaffoldTicket command, ScaffoldTicket scaffoldTicket)
        => WolverineContinue.NoProblems;

    [WolverinePost("/api/scaffold-shapes/close-scaffold-ticket")]
    [EmptyResponse]
    public static ScaffoldTicketClosed? Post(CloseScaffoldTicket command, [WriteModel] ScaffoldTicket scaffoldTicket)
        => new(command.Id);
}

public static class GetScaffoldTicketEndpoint
{
    [WolverineGet("/api/scaffold-ticket/{id}")]
    public static ScaffoldTicket Get([ReadModel] ScaffoldTicket scaffoldTicket)
    {
        return scaffoldTicket;
    }
}
