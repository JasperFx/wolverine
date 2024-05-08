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
}

public record CreateMartenDocument(string Name);
public record InsertMartenDocument(string Name);
public record UpdateMartenDocument(string Name, int Number);
public record DeleteMartenDocument(string Name);

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

    public static void Handle(MartenMessage2 message)
    {
        // Nothing yet
    }
}

public class NamedDocument
{
    public string Id { get; set; }
    public int Number { get; set; }
}