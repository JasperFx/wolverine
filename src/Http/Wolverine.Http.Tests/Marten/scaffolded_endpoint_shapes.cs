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
                q.Excludes.WithCondition("Not a scaffolded endpoint shape", type => !Endpoints.Contains(type)))));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
    }

    private static readonly Type[] Endpoints =
        [typeof(OpenScaffoldLedgerEndpoint), typeof(NoteScaffoldLedgerEndpoint), typeof(NoteScaffoldBothEndpoint)];

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
