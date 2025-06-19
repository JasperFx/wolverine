using IntegrationTests;
using Marten;
using Marten.Events;
using Marten.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests;

public class handler_actions_with_implied_marten_operations_with_tenant_switching : PostgresqlContext, IAsyncLifetime
{
  private IHost _host;
  private IDocumentStore _store;

  public async Task InitializeAsync()
  {
    _host = await Host.CreateDefaultBuilder()
      .UseWolverine(opts =>
      {
        opts.Services
          .AddMarten(o =>
          {
            o.Connection(Servers.PostgresConnectionString);
            o.Policies.AllDocumentsAreMultiTenanted();
            o.Events.StreamIdentity = StreamIdentity.AsString;
          })
          .IntegrateWithWolverine();
        

        opts.Policies.AutoApplyTransactions();
      }).StartAsync();

    _store = _host.Services.GetRequiredService<IDocumentStore>();

    await _store.Advanced.Clean.DeleteDocumentsByTypeAsync(typeof(TenantNamedDocument));
  }

  public async Task DisposeAsync()
  {
    await _host.StopAsync();
    _host.Dispose();
  }

  [Fact]
  public async Task storing_document()
  {
    var tracked = await _host.InvokeMessageAndWaitAsync(new TenantCreateMartenDocument("Aubrey", "green"));

    tracked.Sent.MessagesOf<StoreDoc<TenantNamedDocument>>().ShouldHaveNoMessages();
    tracked.Sent.SingleMessage<TenantMartenMessage2>().Name.ShouldBe("Aubrey");

    using var session = _store.LightweightSession("green");
    var doc = await session.LoadAsync<TenantNamedDocument>("Aubrey");
    doc.ShouldNotBeNull();
  }

  [Fact]
  public async Task insert_document()
  {
    await _host.InvokeMessageAndWaitAsync(new TenantInsertMartenDocument("Declan", "green"));

    using var session = _store.LightweightSession("green");
    var doc = await session.LoadAsync<TenantNamedDocument>("Declan");
    doc.ShouldNotBeNull();

    await Should.ThrowAsync<DocumentAlreadyExistsException>(() =>
      _host.InvokeMessageAndWaitAsync(new TenantInsertMartenDocument("Declan", "green")));
  }

  [Fact]
  public async Task update_document_happy_path()
  {
    await _host.InvokeMessageAndWaitAsync(new TenantInsertMartenDocument("Max", "green"));
    await _host.InvokeMessageAndWaitAsync(new TenantUpdateMartenDocument("Max", 10, "green"));


    using var session = _store.LightweightSession("green");
    var doc = await session.LoadAsync<TenantNamedDocument>("Max");
    doc.Number.ShouldBe(10);


  }

  [Fact]
  public async Task update_document_sad_path()
  {
    await Should.ThrowAsync<NonExistentDocumentException>(() =>
      _host.InvokeMessageAndWaitAsync(new TenantUpdateMartenDocument("Max", 10, "green")));
  }

  [Fact]
  public async Task delete_document()
  {
    await _host.InvokeMessageAndWaitAsync(new TenantInsertMartenDocument("Max", "green"));
    await _host.InvokeMessageAndWaitAsync(new TenantDeleteMartenDocument("Max", "green"));

    using var session = _store.LightweightSession("green");
    var doc = await session.LoadAsync<TenantNamedDocument>("Max");
    doc.ShouldBeNull();
  }
  
}


public record TenantCreateMartenDocument(string Name, string TenantId);
public record TenantInsertMartenDocument(string Name, string TenantId);
public record TenantUpdateMartenDocument(string Name, int Number, string TenantId);
public record TenantDeleteMartenDocument(string Name, string TenantId);

public record TenantMartenMessage2(string Name, string TenantId);

public static class TenantMartenCommandHandler
{
  public static (TenantMartenMessage2, DocumentOp) Handle(TenantCreateMartenDocument command)
  {
    return (new TenantMartenMessage2(command.Name, command.TenantId), MartenOps.Store(new TenantNamedDocument { Id = command.Name }, command.TenantId));
  }

  public static DocumentOp Handle(TenantInsertMartenDocument command)
  {
    return MartenOps.Insert(new TenantNamedDocument { Id = command.Name }, command.TenantId);
  }

  public static async Task<DocumentOp> Handle(TenantUpdateMartenDocument command, IDocumentSession session)
  {
    return MartenOps.Update(new TenantNamedDocument{Id = command.Name, Number = command.Number}, command.TenantId);
  }

  public static async Task<DocumentOp> Handle(TenantDeleteMartenDocument command, IDocumentSession session)
  {
    var doc = await session.ForTenant(command.TenantId).LoadAsync<TenantNamedDocument>(command.Name);

    return MartenOps.Delete(doc, command.TenantId);
  }

  public static void Handle(TenantMartenMessage2 message)
  {
    // Nothing yet
  }
}

public class TenantNamedDocument
{
  public string Id { get; set; }
  public int Number { get; set; }
}