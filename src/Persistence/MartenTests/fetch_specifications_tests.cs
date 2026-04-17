using System.Linq.Expressions;
using IntegrationTests;
using JasperFx;
using JasperFx.Resources;
using Marten;
using Marten.Linq;
using Marten.Services.BatchQuerying;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests;

/// <summary>
/// GH-1561 + GH-2527: Load methods that return Marten compiled queries or
/// IQueryPlans get auto-executed and batched where possible; handler
/// parameters decorated with [FromQuerySpecification] do the same without
/// a Load method.
/// </summary>
public class fetch_specifications_tests : PostgresqlContext, IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "fetch_specs";
                    m.DisableNpgsqlLogging = true;
                }).IntegrateWithWolverine();

                opts.Services.AddResourceSetupOnStartup(StartupAction.ResetState);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<LoadOneNoteHandler>()
                    .IncludeType<ListStarredHandler>()
                    .IncludeType<LoadNoteAndListStarredHandler>()
                    .IncludeType<LoadNoteViaAttributeHandler>()
                    .IncludeType<ListStarredNonBatchableHandler>();
            })
            .StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
    }

    private IDocumentStore Store => _host.Services.GetRequiredService<IDocumentStore>();

    private async Task SeedAsync(params Note[] notes)
    {
        await using var session = Store.LightweightSession();
        foreach (var n in notes) session.Store(n);
        await session.SaveChangesAsync();
    }

    // ─────────────────── compiled query via Load ───────────────────

    [Fact]
    public async Task load_method_returning_compiled_query_runs_and_relays_result_to_handler()
    {
        var id = Guid.NewGuid();
        await SeedAsync(new Note { Id = id, Body = "compiled-load" });

        FetchSpecTestState.LastNoteBody = null;
        await _host.InvokeMessageAndWaitAsync(new LoadOneNote(id));

        FetchSpecTestState.LastNoteBody.ShouldBe("compiled-load");
    }

    // ─────────────────── query plan via Load ───────────────────

    [Fact]
    public async Task load_method_returning_query_plan_runs_and_relays_result_to_handler()
    {
        FetchSpecTestState.LastCount = -1;
        await SeedAsync(
            new Note { Id = Guid.NewGuid(), Body = "plan-1", Starred = true },
            new Note { Id = Guid.NewGuid(), Body = "plan-2", Starred = true },
            new Note { Id = Guid.NewGuid(), Body = "plan-3", Starred = false });

        await _host.InvokeMessageAndWaitAsync(new ListStarred());

        // Shared host across tests may already hold starred notes from earlier
        // test methods — assert at least what we just seeded showed up.
        FetchSpecTestState.LastCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    // ───────────── tuple return: both run batched ─────────────

    [Fact]
    public async Task load_method_returning_tuple_of_specs_runs_both_and_batches()
    {
        var id = Guid.NewGuid();
        await SeedAsync(
            new Note { Id = id, Body = "tuple-target", Starred = true },
            new Note { Id = Guid.NewGuid(), Body = "other-starred", Starred = true });

        FetchSpecTestState.LastNoteBody = null;
        FetchSpecTestState.LastCount = -1;

        await _host.InvokeMessageAndWaitAsync(new LoadNoteAndListStarred(id));

        FetchSpecTestState.LastNoteBody.ShouldBe("tuple-target");
        FetchSpecTestState.LastCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    // ─────────────── [FromQuerySpecification] param ───────────────

    [Fact]
    public async Task from_query_specification_attribute_runs_spec_from_message_fields()
    {
        var id = Guid.NewGuid();
        await SeedAsync(new Note { Id = id, Body = "attribute-target" });

        FetchSpecTestState.LastNoteBody = null;
        await _host.InvokeMessageAndWaitAsync(new LoadNoteViaAttribute(id));

        FetchSpecTestState.LastNoteBody.ShouldBe("attribute-target");
    }

    // ─────────────── non-batchable plan falls back to single fetch ───────────────

    [Fact]
    public async Task non_batchable_query_plan_falls_back_to_single_fetch()
    {
        await SeedAsync(
            new Note { Id = Guid.NewGuid(), Body = "non-batch-a", Starred = true },
            new Note { Id = Guid.NewGuid(), Body = "non-batch-b", Starred = false });

        FetchSpecTestState.LastCount = -1;
        await _host.InvokeMessageAndWaitAsync(new ListStarredNonBatchable());

        FetchSpecTestState.LastCount.ShouldBeGreaterThanOrEqualTo(1);
    }
}

// ─────────────────── Document type ───────────────────

public class Note
{
    public Guid Id { get; set; }
    public string Body { get; set; } = string.Empty;
    public bool Starred { get; set; }
}

// ─────────────────── Messages ───────────────────

public record LoadOneNote(Guid NoteId);
public record ListStarred();
public record LoadNoteAndListStarred(Guid NoteId);
public record LoadNoteViaAttribute(Guid NoteId);
public record ListStarredNonBatchable();

// ─────────────────── Specifications ───────────────────

// Compiled query — always batchable
public class NoteByIdCompiled : ICompiledQuery<Note, Note?>
{
    public Guid NoteId { get; set; }

    public Expression<Func<IMartenQueryable<Note>, Note?>> QueryIs()
    {
        return q => q.FirstOrDefault(x => x.Id == NoteId);
    }
}

// Query plan using QueryListPlan<T> — implements both IQueryPlan<...> AND IBatchQueryPlan<...>
public class StarredNotesPlan : QueryListPlan<Note>
{
    public override IQueryable<Note> Query(IQuerySession session)
        => session.Query<Note>().Where(x => x.Starred);
}

// Plan implementing ONLY IQueryPlan<T> — Wolverine should fall back to single fetch
public class StarredNotesNonBatchPlan : IQueryPlan<IReadOnlyList<Note>>
{
    public async Task<IReadOnlyList<Note>> Fetch(IQuerySession session, CancellationToken token)
    {
        // Uses LINQ ToListAsync; does NOT implement IBatchQueryPlan<T>
        return await session.Query<Note>().Where(x => x.Starred).ToListAsync(token);
    }
}

// ─────────────────── Handlers ───────────────────

// Shared state for test assertions
public static class FetchSpecTestState
{
    public static string? LastNoteBody;
    public static int LastCount = -1;
}

// Compiled query via Load — Wolverine detects ICompiledQuery<Note, Note?>
// on the return, executes it, and passes Note? to Handle.
public class LoadOneNoteHandler
{
    public static NoteByIdCompiled Load(LoadOneNote cmd) => new() { NoteId = cmd.NoteId };

    public static void Handle(LoadOneNote cmd, Note? note)
    {
        FetchSpecTestState.LastNoteBody = note?.Body;
    }
}

// Query plan via Load — batch-capable (QueryListPlan).
public class ListStarredHandler
{
    public static StarredNotesPlan Load(ListStarred cmd) => new();

    public static void Handle(ListStarred cmd, IReadOnlyList<Note> notes)
    {
        FetchSpecTestState.LastCount = notes.Count;
    }
}

// Tuple return — both run, batched into one IBatchedQuery
public class LoadNoteAndListStarredHandler
{
    public static (NoteByIdCompiled, StarredNotesPlan) Load(LoadNoteAndListStarred cmd)
        => (new() { NoteId = cmd.NoteId }, new());

    public static void Handle(LoadNoteAndListStarred cmd, Note? note, IReadOnlyList<Note> notes)
    {
        FetchSpecTestState.LastNoteBody = note?.Body;
        FetchSpecTestState.LastCount = notes.Count;
    }
}

// Attribute-driven — construct spec from message fields, no Load needed.
// Ctor-param "noteId" matches message member "NoteId" (case-insensitively).
public class LoadNoteViaAttributeHandler
{
    public static void Handle(
        LoadNoteViaAttribute cmd,
        [FromQuerySpecification(typeof(NoteByIdCompiled))] Note? note)
    {
        FetchSpecTestState.LastNoteBody = note?.Body;
    }
}

// Non-batchable plan (IQueryPlan<T>-only) — Wolverine falls back to single fetch
public class ListStarredNonBatchableHandler
{
    public static StarredNotesNonBatchPlan Load(ListStarredNonBatchable cmd) => new();

    public static void Handle(ListStarredNonBatchable cmd, IReadOnlyList<Note> notes)
    {
        FetchSpecTestState.LastCount = notes.Count;
    }
}
