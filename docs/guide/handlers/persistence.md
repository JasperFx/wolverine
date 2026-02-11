# Persistence Helpers

Philosophically, Wolverine is trying to enable you to write the message handlers or HTTP endpoint
methods with low ceremony code that's easy to test and easy to reason about. To that end, Wolverine
has quite a few tricks to utilize your persistence tooling from your handler or HTTP endpoint code
without having to directly couple your behavioral code to persistence infrastructure:

* The [storage action side effect model](/guide/handlers/side-effects.html#storage-side-effects) for pure function handlers that involve database "writes"
* The [aggregate handler workflow](/guide/durability/marten/event-sourcing) with Marten for highly testable CQRS + Event Sourcing systems
* Specific [integration with Marten and Wolverine.HTTP](/guide/http/marten)

## Automatically Loading Entities to Method Parameters <Badge type="tip" text="3.6" />

A common need when building Wolverine message handlers or HTTP endpoints is to need to load
an entity object based on an identity value in either the message itself, the HTTP request body, or
an HTTP route argument. In these cases, you'll generally pluck the correct value out of the 
message or route arguments, then call into an EF Core `DbContext` or a Marten/RavenDb `IDocumentSession`
to load the entity for you before proceeding on with your work. Since this usage is so common,
Wolverine has the `[Wolverine.Persistence.Entity]` attribute to just do that for you and have the right entity "pushed" into
your message handler. 

Here's a simple example of a message handler that's also a valid Wolverine.HTTP endpoint using this attribute. First though,
the message type and/or HTTP request body:

<!-- snippet: sample_rename_todo -->
<a id='snippet-sample_rename_todo'></a>
```cs
public record RenameTodo(string Id, string Name);
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L23-L27' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_rename_todo' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

and the handler & endpoint code handling that message type:

<!-- snippet: sample_using_entity_attribute -->
<a id='snippet-sample_using_entity_attribute'></a>
```cs
// Use "Id" as the default member
[WolverinePost("/api/todo/update")]
public static Update<Todo2> Handle(
    // The first argument is always the incoming message
    RenameTodo command, 
    
    // By using this attribute, we're telling Wolverine
    // to load the Todo entity from the configured
    // persistence of the app using a member on the
    // incoming message type
    [Entity] Todo2 todo)
{
    // Do your actual business logic
    todo.Name = command.Name;
    
    // Tell Wolverine that you want this entity
    // updated in persistence
    return Storage.Update(todo);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L55-L77' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_entity_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

In the code above, the `Todo2` argument would be filled by trying to load that `Todo2` entity
from persistence using the value of `RenameTodo.Id`. If you were using Marten as your persistence
mechanism, this would be using `IDocumentSession.LoadAsync<Todo2>(id)` to load the entity with the RavenDb usage being similar. If
you were using EF Core and had an `Todo2DbContext` service registered in your system, it would
be using `Todo2DbContext.FindAsync<Todo2>(id)`. 

By default, Wolverine is assuming that any parameter value marked with `[Entity]` is required, so if the `Todo2` entity was not found in the database, then:

* As a message handler, it will just log that the entity could not be found and otherwise exit cleanly without doing any further processing
* As an HTTP endpoint, the handler would write out a status code of 404 (not found) and exit otherwise

If you need or want any other kind of failure handling on the entity not being found, you'll need to
use explicit code instead, maybe with a `LoadAsync()` "before" method to still keep your main
handler or endpoint method a *pure function*. 

If you genuinely don't need the `[Entity]` value to be required, you can do this instead:

<!-- snippet: sample_using_not_required_entity_attribute -->
<a id='snippet-sample_using_not_required_entity_attribute'></a>
```cs
[WolverinePost("/api/todo/maybecomplete")]
public static IStorageAction<Todo2> Handle(MaybeCompleteTodo command, [Entity(Required = false)] Todo2? todo)
{
    if (todo == null) return Storage.Nothing<Todo2>();
    todo.IsComplete = true;
    return Storage.Update(todo);
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L144-L154' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_using_not_required_entity_attribute' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

So far, all of the examples have depended on a fall back to looking for either a case insensitive match "id"  
match on the message members for message handlers or the route arguments, then request input members
for HTTP endpoints. Wolverine will also look for "[Entity Type Name]Id", so in the case of `Todo2`, it would
look as well for a more specific `Todo2Id` member or route argument for the identity value. 

You can of course override this by just telling Wolverine what member name or route argument name
should have the identity like this:

<!-- snippet: sample_specifying_the_exact_route_argument -->
<a id='snippet-sample_specifying_the_exact_route_argument'></a>
```cs
// Okay, I still used "id", but it *could* be something different here!
[WolverineGet("/api/todo/{id}")]
public static Todo2 Get([Entity("id")] Todo2 todo) => todo;
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Http/WolverineWebApi/Todos/Todo2.cs#L156-L162' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_specifying_the_exact_route_argument' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

If you have any conflict between whether the identity should be found on either the route arguments
or request body, you can specify the identity value source through the `EntityAttribute.ValueSource` property
to one of these values:

<!-- snippet: sample_ValueSource -->
<a id='snippet-sample_ValueSource'></a>
```cs
public enum ValueSource
{
    /// <summary>
    /// This value can be sourced by any mechanism that matches the name. This is the default.
    /// </summary>
    Anything,
    
    /// <summary>
    /// The value should be sourced by a property or field on the message type or HTTP request type
    /// </summary>
    InputMember,
    
    /// <summary>
    /// The value should be sourced by a route argument of an HTTP request
    /// </summary>
    RouteValue,
    
    /// <summary>
    /// The value should be sourced by a query string parameter of an HTTP request
    /// </summary>
    FromQueryString
}
```
<sup><a href='https://github.com/JasperFx/wolverine/blob/main/src/Wolverine/Attributes/ModifyChainAttribute.cs#L18-L43' title='Snippet source file'>snippet source</a> | <a href='#snippet-sample_ValueSource' title='Start of snippet'>anchor</a></sup>
<!-- endSnippet -->

Some other facts to know about `[Entity]` usage:

* Supported by the Marten, EF Core, and RavenDb integration
* For EF Core usage, Wolverine has to be able to figure out which `DbContext` type persists the entity type of the parameter
* In all cases, Wolverine is trying to "know" what the identity type for the entity type is (`Guid`? `int`? Something else?) from the underlying persistence tooling and use that to help parse route arguments as needed
* `[Entity]` cannot support any kind of composite key or identity
* `[Entity]` can be used for both HTTP endpoints and message handler methods
* `[Entity]` can be used for `Before` / `Validate` methods in compound handlers
* If an `[Entity]` attribute is used in the main handler or endpoint method, you can still resolve the same entity type as a parameter to a `Before` method without needing to use the attribute again

::: tip
As with other kinds of Wolverine "magic", lean on the [pre-generated code](/guide/codegen) to let Wolverine explain
what it's doing with your method signatures.
:::
