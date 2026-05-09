using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Polecat;
using Shouldly;
using Wolverine;
using Wolverine.Polecat;
using Wolverine.Tracking;

namespace PolecatTests;

public class handler_actions_with_implied_polecat_operations : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddPolecat(m =>
                    {
                        m.ConnectionString = Servers.SqlServerConnectionString;
                        m.DatabaseSchemaName = "implied_ops";
                    })
                    .IntegrateWithWolverine();

                opts.Policies.UseDurableLocalQueues();
                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();
        await ((DocumentStore)_store).Database.ApplyAllConfiguredChangesToDatabaseAsync();

        await using var session = _store.LightweightSession();
        session.DeleteWhere<PcNamedDocument>(x => true);
        await session.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task storing_document()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new CreatePcDocument("Aubrey"));

        tracked.Sent.MessagesOf<StoreDoc<PcNamedDocument>>().ShouldHaveNoMessages();
        tracked.Sent.SingleMessage<PcMessage2>().Name.ShouldBe("Aubrey");

        await using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<PcNamedDocument>("Aubrey");
        doc.ShouldNotBeNull();
    }

    [Fact]
    public async Task insert_document()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertPcDocument("Declan"));

        await using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<PcNamedDocument>("Declan");
        doc.ShouldNotBeNull();
    }

    [Fact]
    public async Task update_document_happy_path()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertPcDocument("Max"));
        await _host.InvokeMessageAndWaitAsync(new UpdatePcDocument("Max", 10));

        await using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<PcNamedDocument>("Max");
        doc.Number.ShouldBe(10);
    }

    [Fact]
    public async Task delete_document()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertPcDocument("Max"));
        await _host.InvokeMessageAndWaitAsync(new DeletePcDocument("Max"));

        await using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<PcNamedDocument>("Max");
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_int_id()
    {
        await using var session = _store.LightweightSession();

        var id = 2345;

        session.Store(new PcIntIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeletePcDocumentByIntId(id));

        var doc = await session.LoadAsync<PcIntIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_long_id()
    {
        await using var session = _store.LightweightSession();

        var id = 23456L;

        session.Store(new PcLongIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeletePcDocumentByLongId(id));

        var doc = await session.LoadAsync<PcLongIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_guid_id()
    {
        await using var session = _store.LightweightSession();

        var id = Guid.NewGuid();

        session.Store(new PcGuidIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeletePcDocumentByGuidId(id));

        var doc = await session.LoadAsync<PcGuidIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_string_id()
    {
        await using var session = _store.LightweightSession();

        var id = "Max";

        session.Store(new PcStringIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeletePcDocumentByStringId(id));

        var doc = await session.LoadAsync<PcStringIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_where()
    {
        // Clean up first
        await using var cleanSession = _store.LightweightSession();
        cleanSession.DeleteWhere<PcNamedDocument>(x => true);
        await cleanSession.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new InsertPcDocument("foo"));
        await _host.InvokeMessageAndWaitAsync(new InsertPcDocument("bar"));
        await _host.InvokeMessageAndWaitAsync(new InsertPcDocument("baz"));
        await _host.InvokeMessageAndWaitAsync(new DeletePcDocumentsStartingWith("ba"));

        await using var session = _store.LightweightSession();
        // Load each document individually to verify
        var foo = await session.LoadAsync<PcNamedDocument>("foo");
        var bar = await session.LoadAsync<PcNamedDocument>("bar");
        var baz = await session.LoadAsync<PcNamedDocument>("baz");

        foo.ShouldNotBeNull();
        bar.ShouldBeNull();
        baz.ShouldBeNull();
    }

    [Fact]
    public async Task use_enumerable_of_polecatop_as_return_value()
    {
        // Clean up first
        await using var cleanSession = _store.LightweightSession();
        cleanSession.DeleteWhere<PcNamedDocument>(x => true);
        await cleanSession.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new AppendManyPcNamedDocuments(["red", "blue", "green"]));

        await using var session = _store.LightweightSession();

        (await session.LoadAsync<PcNamedDocument>("red")).Number.ShouldBe(1);
        (await session.LoadAsync<PcNamedDocument>("blue")).Number.ShouldBe(2);
        (await session.LoadAsync<PcNamedDocument>("green")).Number.ShouldBe(3);
    }
}

public record CreatePcDocument(string Name);
public record InsertPcDocument(string Name);
public record UpdatePcDocument(string Name, int Number);
public record DeletePcDocument(string Name);
public record DeletePcDocumentByIntId(int DocId);
public record DeletePcDocumentByLongId(long DocId);
public record DeletePcDocumentByGuidId(Guid DocId);
public record DeletePcDocumentByStringId(string DocId);
public record DeletePcDocumentsStartingWith(string Prefix);

public record PcMessage2(string Name);

public record PcMessage3(string Name);

public static class PcCommandHandler
{
    public static (PcMessage2, DocumentOp) Handle(CreatePcDocument command)
    {
        return (new PcMessage2(command.Name), PolecatOps.Store(new PcNamedDocument { Id = command.Name }));
    }

    public static DocumentOp Handle(InsertPcDocument command)
    {
        return PolecatOps.Insert(new PcNamedDocument { Id = command.Name });
    }

    public static async Task<DocumentOp> Handle(UpdatePcDocument command, IDocumentSession session)
    {
        return PolecatOps.Update(new PcNamedDocument { Id = command.Name, Number = command.Number });
    }

    public static async Task<DocumentOp> Handle(DeletePcDocument command, IDocumentSession session)
    {
        var doc = await session.LoadAsync<PcNamedDocument>(command.Name);

        return PolecatOps.Delete(doc);
    }

    public static IPolecatOp Handle(DeletePcDocumentByIntId command)
    {
        return PolecatOps.Delete<PcIntIdDocument>(command.DocId);
    }

    public static IPolecatOp Handle(DeletePcDocumentByLongId command)
    {
        return PolecatOps.Delete<PcLongIdDocument>(command.DocId);
    }

    public static IPolecatOp Handle(DeletePcDocumentByGuidId command)
    {
        return PolecatOps.Delete<PcGuidIdDocument>(command.DocId);
    }

    public static IPolecatOp Handle(DeletePcDocumentByStringId command)
    {
        return PolecatOps.Delete<PcStringIdDocument>(command.DocId);
    }

    public static IPolecatOp Handle(DeletePcDocumentsStartingWith command)
    {
        return PolecatOps.DeleteWhere<PcNamedDocument>(x => x.Id.StartsWith(command.Prefix));
    }

    public static void Handle(PcMessage2 message)
    {
        // Nothing yet
    }
}

public record AppendManyPcNamedDocuments(string[] Names);

public static class AppendManyPcNamedDocumentsHandler
{
    public static IEnumerable<IPolecatOp> Handle(AppendManyPcNamedDocuments command)
    {
        var number = 1;
        foreach (var name in command.Names)
        {
            yield return PolecatOps.Store(new PcNamedDocument { Id = name, Number = number++ });
        }
    }
}

public class PcNamedDocument
{
    public string Id { get; set; } = string.Empty;
    public int Number { get; set; }
}

public class PcIntIdDocument
{
    public int Id { get; set; }
}
public class PcLongIdDocument
{
    public long Id { get; set; }
}
public class PcGuidIdDocument
{
    public Guid Id { get; set; }
}
public class PcStringIdDocument
{
    public string Id { get; set; } = string.Empty;
}
