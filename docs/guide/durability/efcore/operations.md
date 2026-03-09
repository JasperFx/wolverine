# Storage Operations with EF Core

Just know that Wolverine completely supports the concept of [Storage Operations](/guide/handlers/side-effects.html#storage-side-effects) for EF Core. 

Assuming you have an EF Core `DbContext` type like this registered in your system:

<!-- snippet: sample_TodoDbContext -->
<a id='snippet-sample_tododbcontext'></a>
```cs
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
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Persistence/EfCoreTests/using_storage_return_types_and_entity_attributes.cs#L47-L69' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_tododbcontext' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

You can use storage operations in Wolverine message handlers or HTTP endpoints like these samples from the Wolverine
test suite:

<!-- snippet: sample_TodoHandler_to_demonstrate_storage_operations -->
<a id='snippet-sample_todohandler_to_demonstrate_storage_operations'></a>
```cs
public static class TodoHandler
{
    public static Insert<Todo> Handle(CreateTodo command) => Storage.Insert(new Todo
    {
        Id = command.Id,
        Name = command.Name
    });
    
    public static Store<Todo> Handle(CreateTodo2 command) => Storage.Store(new Todo
    {
        Id = command.Id,
        Name = command.Name
    });
    
    // Use "Id" as the default member
    public static Update<Todo> Handle(
        // The first argument is always the incoming message
        RenameTodo command, 
        
        // By using this attribute, we're telling Wolverine
        // to load the Todo entity from the configured
        // persistence of the app using a member on the
        // incoming message type
        [Entity] Todo todo)
    {
        // Do your actual business logic
        todo.Name = command.Name;
        
        // Tell Wolverine that you want this entity
        // updated in persistence
        return Storage.Update(todo);
    }
    
    // Use "TodoId" as the default member
    public static Update<Todo> Handle(RenameTodo2 command, [Entity] Todo todo)
    {
        todo.Name = command.Name;
        return Storage.Update(todo);
    }
    
    // Use the explicit member
    public static Update<Todo> Handle(RenameTodo3 command, [Entity("Identity")] Todo todo)
    {
        todo.Name = command.Name;
        return Storage.Update(todo);
    }

    public static Delete<Todo> Handle(DeleteTodo command, [Entity("Identity")] Todo todo)
    {
        return Storage.Delete(todo);
    }
    
    public static IStorageAction<Todo> Handle(AlterTodo command, [Entity("Identity")] Todo todo)
    {
        switch (command.Action)
        {
            case StorageAction.Delete:
                return Storage.Delete(todo);
            case StorageAction.Update:
                todo.Name = command.Name;
                return Storage.Update(todo);
            case StorageAction.Store:
                todo.Name = command.Name;
                return Storage.Store(todo);
            default:
                return Storage.Nothing<Todo>();
        }
    }

    public static IStorageAction<Todo> Handle(MaybeInsertTodo command)
    {
        if (command.ShouldInsert)
        {
            return Storage.Insert(new Todo { Id = command.Id, Name = command.Name });
        }

        return Storage.Nothing<Todo>();
    }

    public static Insert<Todo>? Handle(ReturnNullInsert command) => null;

    public static IStorageAction<Todo>? Handle(ReturnNullStorageAction command) => null;

    public static IStorageAction<Todo> Handle(CompleteTodo command, [Entity] Todo todo)
    {
        if (todo == null) throw new ArgumentNullException(nameof(todo));
        todo.IsComplete = true;
        return Storage.Update(todo);
    }
    
    public static IStorageAction<Todo> Handle(MaybeCompleteTodo command, [Entity(Required = false)] Todo? todo)
    {
        if (todo == null) return Storage.Nothing<Todo>();
        todo.IsComplete = true;
        return Storage.Update(todo);
    }
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Testing/Wolverine.ComplianceTests/StorageActionCompliance.cs#L294-L394' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_todohandler_to_demonstrate_storage_operations' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

::: warning
When a handler returns an `IStorageAction`, Wolverine automatically
applies [transactional middleware](/guide/durability/marten/transactional-middleware) for that handler — even if the
handler is not explicitly decorated with `[Transactional]` or `AutoApplyTransactions()` is not configured.

This behavior is required because Wolverine needs to automatically call `SaveChangesAsync()` on the EF Core `DbContext`
to persist the storage operation, which should be done within a single transaction together with publication of messages
to the outbox/inbox.
:::

## [Entity]

Wolverine also supports the usage of the `[Entity]` attribute to load entity data by its identity with EF Core. As you'd 
expect, Wolverine can "find" the right EF Core `DbContext` type for the entity type through IoC service registrations. 
The loaded EF core entity does not included related entities. 

For more information on the usage of this attribute see 
[Automatically loading entities to method parameters](/guide/handlers/persistence#automatically-loading-entities-to-method-parameters).
