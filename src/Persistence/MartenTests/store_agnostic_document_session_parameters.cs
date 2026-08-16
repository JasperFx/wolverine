using IntegrationTests;
using JasperFx.Events.Documents;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

/// <summary>
///     GH-3956. A handler can take the store-agnostic <c>JasperFx.Events.Documents</c> document contracts
///     straight as parameters and stay valid on Marten, Polecat and Fisher alike — the document-side
///     counterpart to <see cref="event_store_operations_parameter" />.
/// </summary>
/// <remarks>
///     <para>
///         The parameters always bound: Marten's <c>IDocumentSession</c> implements all three, so a handler
///         declaring one compiled, resolved and ran. What it did not get was a <b>commit</b> — the chain was
///         never recognised as transactional, so the store queued into the session's unit of work and then
///         vanished with no exception. Every assertion here is therefore about the document being readable
///         from a <i>separate</i> session afterwards, never about the parameter being non-null.
///     </para>
/// </remarks>
public class store_agnostic_document_session_parameters : IAsyncLifetime
{
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(SharedDocumentHandler));
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();
                opts.Services.AddMarten(m =>
                {
                    m.DisableNpgsqlLogging = true;
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "shared_doc_ops_param";
                }).IntegrateWithWolverine().UseLightweightSessions();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task session_operations_parameter_is_committed()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StoreViaSessionOperations(id, "session-ops"));

        await using var session = _host.DocumentStore().LightweightSession();
        var note = await session.LoadAsync<SharedNote>(id, TestContext.Current.CancellationToken);

        note.ShouldNotBeNull();
        note.Text.ShouldBe("session-ops");
    }

    [Fact]
    public async Task write_operations_parameter_is_committed()
    {
        var id = Guid.NewGuid();

        await _host.InvokeMessageAndWaitAsync(new StoreViaWriteOperations(id, "write-ops"));

        await using var session = _host.DocumentStore().LightweightSession();
        var note = await session.LoadAsync<SharedNote>(id, TestContext.Current.CancellationToken);

        note.ShouldNotBeNull();
        note.Text.ShouldBe("write-ops");
    }

    /// <summary>
    ///     The read and write contracts must be satisfied by the <b>same</b> session variable. If the read side
    ///     resolved to its own query session instead, a handler that writes and then reads would silently be
    ///     reading from a different unit of work.
    /// </summary>
    [Fact]
    public async Task read_and_write_operations_resolve_to_one_session()
    {
        var result = await _host.MessageBus()
            .InvokeAsync<DocumentSessionsCompared>(new CompareDocumentSessions(),
                TestContext.Current.CancellationToken);

        result.ShouldNotBeNull();
        result.SameInstance.ShouldBeTrue();
    }

    /// <summary>
    ///     A read-only parameter is not evidence that the chain writes anything, so it must NOT drag the chain
    ///     into a transaction — but it still has to bind and read.
    /// </summary>
    [Fact]
    public async Task read_operations_parameter_can_load_a_document()
    {
        var id = Guid.NewGuid();

        await using (var seed = _host.DocumentStore().LightweightSession())
        {
            seed.Store(new SharedNote { Id = id, Text = "seeded" });
            await seed.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var result = await _host.MessageBus()
            .InvokeAsync<SharedNoteFound>(new ReadSharedNote(id), TestContext.Current.CancellationToken);

        result.Text.ShouldBe("seeded");
    }

    /// <summary>
    ///     Marten registers <c>IDocumentStore</c>, never the lifted factory contract it already implements, so
    ///     this failed to resolve on a stock host before GH-3956.
    /// </summary>
    [Fact]
    public void document_session_factory_contracts_are_registered()
    {
        var factory = _host.Services.GetRequiredService<IDocumentSessionFactory>();
        factory.ShouldBeSameAs(_host.Services.GetRequiredService<IDocumentStore>());

        _host.Services.GetRequiredService<IDocumentSessionFactory<IDocumentSession, IQuerySession>>()
            .ShouldBeSameAs(_host.Services.GetRequiredService<IDocumentStore>());
    }
}

public class SharedNote
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
}

public record StoreViaSessionOperations(Guid Id, string Text);

public record StoreViaWriteOperations(Guid Id, string Text);

public record CompareDocumentSessions;

public record DocumentSessionsCompared(bool SameInstance);

public record ReadSharedNote(Guid Id);

public record SharedNoteFound(string Text);

[WolverineIgnore]
public static class SharedDocumentHandler
{
    // Every signature below names only JasperFx.Events.Documents types -- nothing Marten-specific --
    // which is the entire point of the issue
    public static void Handle(StoreViaSessionOperations command, IDocumentSessionOperations session)
    {
        session.Store(new SharedNote { Id = command.Id, Text = command.Text });
    }

    public static void Handle(StoreViaWriteOperations command, IDocumentWriteOperations writes)
    {
        writes.Store(new SharedNote { Id = command.Id, Text = command.Text });
    }

    public static DocumentSessionsCompared Handle(CompareDocumentSessions command,
        IDocumentWriteOperations writes, IDocumentReadOperations reads)
    {
        return new DocumentSessionsCompared(ReferenceEquals(writes, reads));
    }

    public static async Task<SharedNoteFound> Handle(ReadSharedNote command, IDocumentReadOperations reads)
    {
        var note = await reads.LoadAsync<SharedNote>(command.Id);
        return new SharedNoteFound(note!.Text);
    }
}
