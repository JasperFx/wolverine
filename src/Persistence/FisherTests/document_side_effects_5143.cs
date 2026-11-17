using JasperFx;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Fisher;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Fisher;
using Wolverine.Tracking;

namespace FisherTests;

// GH-5143: the store agnostic document and stream side effects. The handler below is character for character
// what the Marten and Fisher suites run.
public class document_side_effects_5143 : IAsyncLifetime
{
    private FisherTestDatabase _database = null!;
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _database = Servers.CreateDatabase("doc_side_effects5143");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SideEffectNoteHandler5143));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Services.AddFisher(m =>
                    {
                        m.Connection(_database.ConnectionString);
                        m.AutoCreateSchemaObjects = AutoCreate.All;
                    })
                    .ApplyAllDatabaseChangesOnStartup()
                    .IntegrateWithWolverine();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
        _database.Dispose();
    }

    private IDocumentStore theStore => _host.Services.GetRequiredService<IDocumentStore>();

    private async Task storeAsync(params SideEffectNote5143[] notes)
    {
        await using var session = theStore.LightweightSession();
        session.Store(notes);
        await session.SaveChangesAsync();
    }

    [Fact]
    public async Task delete_by_id_deletes_the_document()
    {
        var note = new SideEffectNote5143 { Id = Guid.NewGuid(), Category = "keep" };
        await storeAsync(note);

        await _host.InvokeMessageAndWaitAsync(new DeleteSideEffectNote5143(note.Id));

        await using var session = theStore.QuerySession();
        (await session.LoadAsync<SideEffectNote5143>(note.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task delete_where_deletes_only_the_matching_documents()
    {
        var category = Guid.NewGuid().ToString();
        var doomed = new SideEffectNote5143 { Id = Guid.NewGuid(), Category = category };
        var kept = new SideEffectNote5143 { Id = Guid.NewGuid(), Category = "keep" };
        await storeAsync(doomed, kept);

        await _host.InvokeMessageAndWaitAsync(new DeleteSideEffectNotesIn5143(category));

        await using var session = theStore.QuerySession();
        (await session.LoadAsync<SideEffectNote5143>(doomed.Id, TestContext.Current.CancellationToken)).ShouldBeNull();
        (await session.LoadAsync<SideEffectNote5143>(kept.Id, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    [Fact]
    public async Task archive_stream_archives_the_stream()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartSideEffectStream5143(id));

        await _host.InvokeMessageAndWaitAsync(new ArchiveSideEffectStream5143(id));

        await using var session = theStore.QuerySession();
        var state = await session.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken);
        state.ShouldNotBeNull().IsArchived.ShouldBeTrue();
    }

    [Fact]
    public async Task tombstone_stream_removes_the_stream()
    {
        var id = Guid.NewGuid();
        await _host.InvokeMessageAndWaitAsync(new StartSideEffectStream5143(id));

        await _host.InvokeMessageAndWaitAsync(new TombstoneSideEffectStream5143(id));

        await using var session = theStore.QuerySession();
        (await session.Events.FetchStreamStateAsync(id, TestContext.Current.CancellationToken)).ShouldBeNull();
    }
}

public class SideEffectNote5143
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
}

public record SideEffectStreamStarted5143;

public record DeleteSideEffectNote5143(Guid Id);

public record DeleteSideEffectNotesIn5143(string Category);

public record StartSideEffectStream5143(Guid Id);

public record ArchiveSideEffectStream5143(Guid Id);

public record TombstoneSideEffectStream5143(Guid Id);

// [WolverineIgnore] for the same reason as the event_side_effects handlers: these throw at bootstrap in a host
// with no event store, and this assembly is shared.
[WolverineIgnore]
public static class SideEffectNoteHandler5143
{
    public static DeleteById<SideEffectNote5143> Handle(DeleteSideEffectNote5143 command)
        => Storage.DeleteById<SideEffectNote5143>(command.Id);

    public static DeleteWhere<SideEffectNote5143> Handle(DeleteSideEffectNotesIn5143 command)
        => Storage.DeleteWhere<SideEffectNote5143>(x => x.Category == command.Category);

    public static StartStream Handle(StartSideEffectStream5143 command)
        => Storage.StartStream(command.Id, new SideEffectStreamStarted5143());

    public static ArchiveEventStream Handle(ArchiveSideEffectStream5143 command)
        => Storage.ArchiveStream(command.Id);

    public static TombstoneEventStream Handle(TombstoneSideEffectStream5143 command)
        => Storage.TombstoneStream(command.Id);
}
