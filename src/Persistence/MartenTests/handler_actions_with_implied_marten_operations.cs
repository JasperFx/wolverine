using IntegrationTests;
using Marten;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;
using Shouldly;

namespace MartenTests;

public class handler_actions_with_implied_marten_operations : PostgresqlContext, IAsyncLifetime
{
    private IHost _host;
    private IDocumentStore _store;

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services
                    .AddMarten(Servers.PostgresConnectionString)
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
            }).StartAsync();

        _store = _host.Services.GetRequiredService<IDocumentStore>();

        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(IntIdDocument));
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(LongIdDocument));
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(GuidIdDocument));
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(StringIdDocument));
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task storing_document()
    {
        var tracked = await _host.InvokeMessageAndWaitAsync(new CreateMartenDocument("Aubrey"));

        tracked.Sent.MessagesOf<StoreDoc<NamedDocument>>().ShouldHaveNoMessages();
        tracked.Sent.SingleMessage<MartenMessage2>().Name.ShouldBe("Aubrey");

        using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<NamedDocument>("Aubrey");
        doc.ShouldNotBeNull();
    }

    [Fact]
    public async Task insert_document()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertMartenDocument("Declan"));

        using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<NamedDocument>("Declan");
        doc.ShouldNotBeNull();

        await Should.ThrowAsync<DocumentAlreadyExistsException>(() =>
            _host.InvokeMessageAndWaitAsync(new InsertMartenDocument("Declan")));


    }

    [Fact]
    public async Task update_document_happy_path()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertMartenDocument("Max"));
        await _host.InvokeMessageAndWaitAsync(new UpdateMartenDocument("Max", 10));


        using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<NamedDocument>("Max");
        doc.Number.ShouldBe(10);


    }

    [Fact]
    public async Task update_document_sad_path()
    {
        await Should.ThrowAsync<NonExistentDocumentException>(() =>
            _host.InvokeMessageAndWaitAsync(new UpdateMartenDocument("Max", 10)));
    }

    [Fact]
    public async Task delete_document()
    {
        await _host.InvokeMessageAndWaitAsync(new InsertMartenDocument("Max"));
        await _host.InvokeMessageAndWaitAsync(new DeleteMartenDocument("Max"));

        using var session = _store.LightweightSession();
        var doc = await session.LoadAsync<NamedDocument>("Max");
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_int_id()
    {
        await using var session = _store.LightweightSession();

        var id = 2345;

        session.Store(new IntIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeleteMartenDocumentByIntId(id));

        var doc = await session.LoadAsync<IntIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_long_id()
    {
        await using var session = _store.LightweightSession();

        var id = 23456L;

        session.Store(new LongIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeleteMartenDocumentByLongId(id));

        var doc = await session.LoadAsync<LongIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_guid_id()
    {
        await using var session = _store.LightweightSession();

        var id = Guid.NewGuid();

        session.Store(new GuidIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeleteMartenDocumentByGuidId(id));

        var doc = await session.LoadAsync<GuidIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_document_by_string_id()
    {
        await using var session = _store.LightweightSession();

        var id = "Max";

        session.Store(new StringIdDocument { Id = id });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeleteMartenDocumentByStringId(id));

        var doc = await session.LoadAsync<StringIdDocument>(id);
        doc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_documents_by_object_ids()
    {
        await using var session = _store.LightweightSession();

        var intId = 1234;
        var longId = 56789L;
        var guidId = Guid.NewGuid();
        var stringId = "Max";

        session.Store(new IntIdDocument { Id = intId });
        session.Store(new LongIdDocument { Id = longId });
        session.Store(new GuidIdDocument { Id = guidId });
        session.Store(new StringIdDocument { Id = stringId });
        await session.SaveChangesAsync();

        await _host.InvokeMessageAndWaitAsync(new DeleteMartenDocumentsByObjectIds(intId, longId, guidId, stringId));

        var intDoc = await session.LoadAsync<IntIdDocument>(intId);
        var longDoc = await session.LoadAsync<LongIdDocument>(longId);
        var guidDoc = await session.LoadAsync<GuidIdDocument>(guidId);
        var stringDoc = await session.LoadAsync<StringIdDocument>(stringId);
        intDoc.ShouldBeNull();
        longDoc.ShouldBeNull();
        guidDoc.ShouldBeNull();
        stringDoc.ShouldBeNull();
    }

    [Fact]
    public async Task delete_documents_by_object_ids_throws_when_invalid_id_type_provided()
    {
        var invalidId = new object();

        await Should.ThrowAsync<DocumentIdTypeMismatchException>(() =>
            _host.InvokeMessageAndWaitAsync(
                new DeleteMartenDocumentsByObjectIds(invalidId, invalidId, invalidId, invalidId)));
    }
    
    [Fact]
    public async Task delete_document_where()
    {
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));

        await _host.InvokeMessageAndWaitAsync(new InsertMartenDocument("foo"));
        await _host.InvokeMessageAndWaitAsync(new InsertMartenDocument("bar"));
        await _host.InvokeMessageAndWaitAsync(new InsertMartenDocument("baz"));
        await _host.InvokeMessageAndWaitAsync(new DeleteMartenDocumentsStartingWith("ba"));

        await using var session = _store.LightweightSession();
        var docs = await session.Query<NamedDocument>().ToListAsync();
        
        docs.ShouldHaveSingleItem().Id.ShouldBe("foo");
    }

    [Fact]
    public async Task use_enumerable_of_imartenop_as_return_value()
    {
        await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(NamedDocument));

        await _host.InvokeMessageAndWaitAsync(new AppendManyNamedDocuments(["red", "blue", "green"]));

        using var session = _store.LightweightSession();
        
        (await session.LoadAsync<NamedDocument>("red")).Number.ShouldBe(1);
        (await session.LoadAsync<NamedDocument>("blue")).Number.ShouldBe(2);
        (await session.LoadAsync<NamedDocument>("green")).Number.ShouldBe(3);
    }
}

public record CreateMartenDocument(string Name);
public record InsertMartenDocument(string Name);
public record UpdateMartenDocument(string Name, int Number);
public record DeleteMartenDocument(string Name);
public record DeleteMartenDocumentByIntId(int DocId);
public record DeleteMartenDocumentByLongId(long DocId);
public record DeleteMartenDocumentByGuidId(Guid DocId);
public record DeleteMartenDocumentByStringId(string DocId);
public record DeleteMartenDocumentsByObjectIds(object IntId, object LongId, object GuidId, object StringId);
public record DeleteMartenDocumentsStartingWith(string Prefix);

public record MartenMessage2(string Name);

public record MartenMessage3(string Name);

public record MartenMessage4(string Name);

public record MartenCommand1(string Name);

public record MartenCommand2(string Name);

public record MartenCommand3(string Name);

public record MartenCommand4(string Name);

public static class MartenCommandHandler
{
    public static (MartenMessage2, DocumentOp) Handle(CreateMartenDocument command)
    {
        return (new MartenMessage2(command.Name), MartenOps.Store(new NamedDocument { Id = command.Name }));
    }

    public static DocumentOp Handle(InsertMartenDocument command)
    {
        return MartenOps.Insert(new NamedDocument { Id = command.Name });
    }

    public static async Task<DocumentOp> Handle(UpdateMartenDocument command, IDocumentSession session)
    {
        return MartenOps.Update(new NamedDocument{Id = command.Name, Number = command.Number});
    }

    public static async Task<DocumentOp> Handle(DeleteMartenDocument command, IDocumentSession session)
    {
        var doc = await session.LoadAsync<NamedDocument>(command.Name);

        return MartenOps.Delete(doc);
    }

    public static IMartenOp Handle(DeleteMartenDocumentByIntId command)
    {
        return MartenOps.Delete<IntIdDocument>(command.DocId);
    }

    public static IMartenOp Handle(DeleteMartenDocumentByLongId command)
    {
        return MartenOps.Delete<LongIdDocument>(command.DocId);
    }

    public static IMartenOp Handle(DeleteMartenDocumentByGuidId command)
    {
        return MartenOps.Delete<GuidIdDocument>(command.DocId);
    }

    public static IMartenOp Handle(DeleteMartenDocumentByStringId command)
    {
        return MartenOps.Delete<StringIdDocument>(command.DocId);
    }

    public static IEnumerable<IMartenOp> Handle(DeleteMartenDocumentsByObjectIds command)
    {
        yield return MartenOps.Delete<IntIdDocument>(command.IntId);
        yield return MartenOps.Delete<LongIdDocument>(command.LongId);
        yield return MartenOps.Delete<GuidIdDocument>(command.GuidId);
        yield return MartenOps.Delete<StringIdDocument>(command.StringId);
    }

    public static IMartenOp Handle(DeleteMartenDocumentsStartingWith command)
    {
        return MartenOps.DeleteWhere<NamedDocument>(x => x.Id.StartsWith(command.Prefix));
    }

    public static void Handle(MartenMessage2 message)
    {
        // Nothing yet
    }
}

public record AppendManyNamedDocuments(string[] Names);

public static class AppendManyNamedDocumentsHandler
{
    #region sample_using_ienumerable_of_martenop_as_side_effect

    // Just keep in mind that this "example" was rigged up for test coverage
    public static IEnumerable<IMartenOp> Handle(AppendManyNamedDocuments command)
    {
        var number = 1;
        foreach (var name in command.Names)
        {
            yield return MartenOps.Store(new NamedDocument{Id = name, Number = number++});
        }
    }

    #endregion
}

public class NamedDocument
{
    public string Id { get; set; }
    public int Number { get; set; }
}

public class IntIdDocument
{
    public int Id { get; set; }
}
public class LongIdDocument
{
    public long Id { get; set; }
}
public class GuidIdDocument
{
    public Guid Id { get; set; }
}
public class StringIdDocument
{
    public string Id { get; set; }
}