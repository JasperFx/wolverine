using JasperFx.Events.Documents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace InMemoryTests;

// Storage.StartStream / AppendEvents, [Entity] and IStorageAction<T>, the declarative reads, and a handler
// that takes the store-agnostic session contract directly -- every one of them naming no store.
public class storage_side_effects : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await TestHosts.StartAsync(opts =>
        {
            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(InvoiceHandler))
                .IncludeType(typeof(TodoHandler));

            opts.Policies.AutoApplyTransactions();
        });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private IDocumentSessionFactory Sessions => theHost.Services.GetRequiredService<IDocumentSessionFactory>();

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task start_stream_creates_the_stream_and_its_events()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new CreateInvoice(id, 100));

        await using var session = Sessions.QuerySession();
        var events = await session.Events.FetchStreamAsync(id, token: Token);

        events.Count.ShouldBe(1);
        events[0].Data.ShouldBeOfType<InvoiceCreated>().Amount.ShouldBe(100);
    }

    [Fact]
    public async Task append_events_adds_to_an_existing_stream()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new CreateInvoice(id, 100));

        await theHost.InvokeMessageAndWaitAsync(new ApproveInvoice(id, "kareem"));

        await using var session = Sessions.QuerySession();
        var events = await session.Events.FetchStreamAsync(id, token: Token);

        events.Count.ShouldBe(2);
        events[1].Data.ShouldBeOfType<InvoiceApproved>().ApprovedBy.ShouldBe("kareem");
    }

    [Fact]
    public async Task a_storage_action_inserts_and_entity_loads_it_back()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new AddTodo(id, "write the docs"));
        await theHost.InvokeMessageAndWaitAsync(new RenameTodo(id, "write better docs"));

        await using var session = Sessions.QuerySession();
        (await session.LoadAsync<Todo>(id, Token))!.Name.ShouldBe("write better docs");
    }

    [Fact]
    public async Task a_storage_action_deletes()
    {
        var id = Guid.NewGuid();
        await theHost.InvokeMessageAndWaitAsync(new AddTodo(id, "doomed"));

        await theHost.InvokeMessageAndWaitAsync(new DeleteTodo(id));

        await using var session = Sessions.QuerySession();
        (await session.LoadAsync<Todo>(id, Token)).ShouldBeNull();
    }

    [Fact]
    public async Task a_required_entity_that_is_missing_stops_the_handler()
    {
        TodoHandler.Renamed = 0;

        await theHost.InvokeMessageAndWaitAsync(new RenameTodo(Guid.NewGuid(), "nothing to rename"));

        TodoHandler.Renamed.ShouldBe(0);
    }

    [Fact]
    public async Task the_all_read_sees_every_document()
    {
        var marker = Guid.NewGuid().ToString();
        await theHost.InvokeMessageAndWaitAsync(new AddTodo(Guid.NewGuid(), marker));
        await theHost.InvokeMessageAndWaitAsync(new AddTodo(Guid.NewGuid(), marker));

        await theHost.InvokeMessageAndWaitAsync(new CountTodos(marker));

        TodoHandler.LastCount.ShouldBe(2);
    }

    [Fact]
    public async Task a_handler_taking_the_session_contract_is_committed_by_auto_transactions()
    {
        var id = Guid.NewGuid();

        await theHost.InvokeMessageAndWaitAsync(new AddTodoThroughTheSession(id, "direct"));

        await using var session = Sessions.QuerySession();
        (await session.LoadAsync<Todo>(id, Token))!.Name.ShouldBe("direct");
    }
}

public record InvoiceCreated(decimal Amount);

public record InvoiceApproved(string ApprovedBy);

public record CreateInvoice(Guid Id, decimal Amount);

public record ApproveInvoice(Guid Id, string ApprovedBy);

[WolverineIgnore]
public static class InvoiceHandler
{
    public static StartStream Handle(CreateInvoice command)
        => Storage.StartStream(command.Id, new InvoiceCreated(command.Amount));

    public static AppendEvents Handle(ApproveInvoice command)
        => Storage.AppendEvents(command.Id, new InvoiceApproved(command.ApprovedBy));
}

public class Todo
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
}

public record AddTodo(Guid Id, string Name);

public record RenameTodo(Guid Id, string Name);

public record DeleteTodo(Guid Id);

public record CountTodos(string Name);

public record AddTodoThroughTheSession(Guid Id, string Name);

[WolverineIgnore]
public static class TodoHandler
{
    public static int Renamed { get; set; }
    public static int LastCount { get; private set; }

    public static IStorageAction<Todo> Handle(AddTodo command)
        => Storage.Insert(new Todo { Id = command.Id, Name = command.Name });

    public static IStorageAction<Todo> Handle(RenameTodo command, [Entity] Todo todo)
    {
        Renamed++;
        todo.Name = command.Name;
        return Storage.Update(todo);
    }

    public static IStorageAction<Todo> Handle(DeleteTodo command, [Entity] Todo todo) => Storage.Delete(todo);

    public static void Handle(CountTodos command, [All] IReadOnlyList<Todo> all)
        => LastCount = all.Count(x => x.Name == command.Name);

    public static void Handle(AddTodoThroughTheSession command, IDocumentSessionOperations session)
        => session.Store(new Todo { Id = command.Id, Name = command.Name });
}
