namespace WolverineFSharpSample

open System
open Microsoft.EntityFrameworkCore

/// The EF Core entity persisted by the sample.
[<CLIMutable>]
type Item = { Id: Guid; Name: string }

/// The command handled by CreateItemHandler.
type CreateItemCommand = { Name: string }

/// The event cascaded out after the item is created.
type ItemCreated = { Id: Guid }

/// The sample's EF Core DbContext. Wolverine's EF Core integration enrolls this in its
/// transactional outbox; the generated handler adapter constructs/commits an EfCoreEnvelopeTransaction
/// around it.
type ItemsDbContext(options: DbContextOptions<ItemsDbContext>) =
    inherit DbContext(options)

    [<DefaultValue>]
    val mutable private items: DbSet<Item>

    member this.Items
        with get () = this.items
        and set v = this.items <- v
