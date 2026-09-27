using IntegrationTests;
using JasperFx.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.ComplianceTests;
using Wolverine.EntityFrameworkCore;
using Wolverine.Persistence;
using Wolverine.Postgresql;
using Wolverine.SqlServer;
using Wolverine.Tracking;

namespace EfCoreTests;

/// <summary>
///     GH-4635. The storage-action compliance battery used to exist exactly once for EF Core, against
///     SQL Server in <see cref="TransactionMiddlewareMode.Eager" /> mode. Both axes matter: the two modes
///     emit genuinely different frames (see <c>applyEagerCommitOrLightweightFlush</c>), and the two
///     databases differ in identifier casing, schema handling and concurrency error surfacing. #4613 was
///     found by a user in precisely the part of that grid nothing ran in.
/// </summary>
/// <remarks>
///     The two extra facts below are deliberately here rather than on the shared
///     <see cref="StorageActionCompliance" /> base. They are about EF Core's change tracker — an identity
///     conflict between two instances of one key, and a zero-row UPDATE/DELETE becoming a
///     <see cref="DbUpdateConcurrencyException" /> — and every other store the base battery runs against
///     (Marten, Polecat, RavenDb, Redis, S3, Blob Storage) has different, equally correct answers for
///     both. Adding them to the shared base would assert EF Core's semantics on six stores that do not
///     share them.
/// </remarks>
public abstract class EfCoreStorageActionCompliance : StorageActionCompliance
{
    protected abstract TransactionMiddlewareMode Mode { get; }

    protected abstract void configureDatabase(WolverineOptions opts);

    protected sealed override void configureWolverine(WolverineOptions opts)
    {
        opts.Discovery
            .IncludeType(typeof(EfCoreDetachedTodoHandler));

        configureDatabase(opts);

        opts.UseEntityFrameworkCoreTransactions(Mode);
        opts.UseEntityFrameworkCoreWolverineManagedMigrations();
        opts.Services.AddResourceSetupOnStartup();
    }

    public override async Task<Todo?> Load(string id)
    {
        using var scope = Host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        return await context.Todos.FindAsync(id);
    }

    public override async Task Persist(Todo todo)
    {
        using var scope = Host.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<TodoDbContext>();
        context.Todos.Add(todo);
        await context.SaveChangesAsync();
    }

    /// <summary>
    ///     GH-4635. <c>[Entity]</c> loads the Todo through the same scoped DbContext the handler's storage
    ///     action is applied to, so that instance is already in the change tracker. Returning an
    ///     <c>Update&lt;Todo&gt;</c> for a SECOND instance carrying the same key — the shape you get when a
    ///     handler rebuilds the entity from the message, or maps a DTO back — is the case EF answers with
    ///     "another instance with the same key value is already being tracked". A store with no identity
    ///     map simply writes the second instance, which is why nothing caught this.
    /// </summary>
    /// <remarks>
    ///     <b>This pins a defect, not a contract.</b> The write does not happen and the caller gets a raw
    ///     EF <see cref="InvalidOperationException" /> out of
    ///     <c>EfCoreStorageActionApplier.UpdateAsync</c>, whose <c>isTracked</c> check compares by
    ///     REFERENCE, so a same-key different-instance sails past it into <c>DbContext.Update()</c>. The
    ///     fix is for <c>UpdateAsync</c> to look the key up in the change tracker and
    ///     <c>CurrentValues.SetValues</c> onto the entry it finds — the same move <c>StoreAsync</c>
    ///     already makes after <c>FindAsync</c>. Left as a pinned assertion here because #4635 is a
    ///     coverage issue: the point is to make the shape visible and regression-guarded so it can be
    ///     fixed on purpose rather than folded into a test PR.
    /// </remarks>
    [Fact]
    public async Task update_a_detached_instance_whose_key_is_already_tracked()
    {
        var id = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodo(id, "Write docs"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Host.InvokeMessageAndWaitAsync(
                new RenameTodoWithTrackedConflict(id, "Renamed past the identity map")));

        ex.Message.ShouldContain("another instance with the same key value");

        // ...and the rename did not happen
        (await Load(id))!.Name.ShouldBe("Write docs");
    }

    /// <summary>
    ///     GH-4635. The <c>Delete&lt;T&gt;</c> half of the same shape: the row to delete was loaded and is
    ///     tracked, and the handler returns a delete for a different instance of the same key.
    /// </summary>
    /// <remarks>
    ///     Same defect as <see cref="update_a_detached_instance_whose_key_is_already_tracked" /> and also
    ///     pinned rather than fixed, but it fails one layer earlier: the declared <c>Delete&lt;T&gt;</c>
    ///     return type is served by <c>EFCorePersistenceFrameProvider.DetermineDeleteFrame</c>, which
    ///     emits a bare <c>DbContext.Remove()</c> with no tracking check at all.
    /// </remarks>
    [Fact]
    public async Task delete_a_detached_instance_whose_key_is_already_tracked()
    {
        var id = Guid.NewGuid().ToString();
        await Host.InvokeMessageAndWaitAsync(new CreateTodo(id, "Write docs"));

        var ex = await Should.ThrowAsync<InvalidOperationException>(async () =>
            await Host.InvokeMessageAndWaitAsync(new DeleteTodoWithTrackedConflict(id)));

        ex.Message.ShouldContain("another instance with the same key value");

        // ...and the row is still there
        (await Load(id)).ShouldNotBeNull();
    }

    /// <summary>
    ///     GH-4635. Deleting a row that is not there. EF attaches the detached instance as Deleted and
    ///     issues a DELETE whose affected-row count is zero, which EF reports as a
    ///     <see cref="DbUpdateConcurrencyException" /> rather than as a no-op. Whatever the answer is, a
    ///     delete of a missing id is a shape real applications hit (a duplicate delete command, a retry
    ///     after a successful-but-unacknowledged handler run), and it had no coverage at all.
    /// </summary>
    [Fact]
    public async Task delete_a_row_that_does_not_exist()
    {
        var missing = Guid.NewGuid().ToString();

        await Should.ThrowAsync<DbUpdateConcurrencyException>(async () =>
            await Host.InvokeMessageAndWaitAsync(new DeleteMissingTodo(missing)));

        (await Load(missing)).ShouldBeNull();
    }
}

public class storage_actions_on_sqlserver_eager : EfCoreStorageActionCompliance
{
    protected override TransactionMiddlewareMode Mode => TransactionMiddlewareMode.Eager;

    protected override void configureDatabase(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<TodoDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString), "wolverine");

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
    }
}

public class storage_actions_on_sqlserver_lightweight : EfCoreStorageActionCompliance
{
    protected override TransactionMiddlewareMode Mode => TransactionMiddlewareMode.Lightweight;

    protected override void configureDatabase(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<TodoDbContext>(
            x => x.UseSqlServer(Servers.SqlServerConnectionString), "wolverine");

        opts.PersistMessagesWithSqlServer(Servers.SqlServerConnectionString);
    }
}

public class storage_actions_on_postgresql_eager : EfCoreStorageActionCompliance
{
    protected override TransactionMiddlewareMode Mode => TransactionMiddlewareMode.Eager;

    protected override void configureDatabase(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<TodoDbContext>(
            x => x.UseNpgsql(Servers.PostgresConnectionString), "wolverine");

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
    }
}

public class storage_actions_on_postgresql_lightweight : EfCoreStorageActionCompliance
{
    protected override TransactionMiddlewareMode Mode => TransactionMiddlewareMode.Lightweight;

    protected override void configureDatabase(WolverineOptions opts)
    {
        opts.Services.AddDbContextWithWolverineIntegration<TodoDbContext>(
            x => x.UseNpgsql(Servers.PostgresConnectionString), "wolverine");

        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
    }
}

#region sample_tododbcontext
public class TodoDbContext : DbContext
{
    public TodoDbContext(DbContextOptions<TodoDbContext> options) : base(options)
    {
    }

    public DbSet<Todo> Todos { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Todo>(map =>
        {
            map.ToTable("todos", "todo_app");
            map.HasKey(x => x.Id);
            map.Property(x => x.Name);
            map.Property(x => x.IsComplete).HasColumnName("is_complete");
        });
    }
}

#endregion

// GH-4635. The entity the handler hands back is a DIFFERENT instance from the one [Entity] loaded,
// but carries the same key -- so the DbContext's identity map already holds that key.
public record RenameTodoWithTrackedConflict(string Id, string Name);

public record DeleteTodoWithTrackedConflict(string Id);

public record DeleteMissingTodo(string Id);

/// <summary>
///     GH-4635. Deliberately outside the shared <c>TodoHandler</c> in Wolverine.ComplianceTests: these
///     shapes are only asserted for EF Core, so the handlers only need to exist for the EF suites.
/// </summary>
/// <remarks>
///     <c>[WolverineIgnore]</c> matters here. The shared <c>TodoHandler</c> lives in another assembly and
///     is never scanned; this one does not, so without the attribute conventional discovery hands a
///     <c>[Entity] Todo</c> handler to every other host in this project and each one fails to start with
///     "unable to determine a persistence provider for entity type ... Todo". The compliance fixture
///     names the type explicitly with <c>IncludeType</c>, which wins over the attribute.
/// </remarks>
[WolverineIgnore]
public static class EfCoreDetachedTodoHandler
{
    public static Update<Todo> Handle(RenameTodoWithTrackedConflict command, [Entity] Todo todo) =>
        Storage.Update(new Todo { Id = command.Id, Name = command.Name, IsComplete = todo.IsComplete });

    public static Delete<Todo> Handle(DeleteTodoWithTrackedConflict command, [Entity] Todo todo) =>
        Storage.Delete(new Todo { Id = command.Id, Name = todo.Name, IsComplete = todo.IsComplete });

    public static Delete<Todo> Handle(DeleteMissingTodo command) =>
        Storage.Delete(new Todo { Id = command.Id });
}
