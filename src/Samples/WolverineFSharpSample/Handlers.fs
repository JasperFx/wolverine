namespace WolverineFSharpSample

open System
open Wolverine.Attributes

/// An EF Core transactional message handler written in F#. Mirrors the C# EFCoreSample handler:
/// [<Transactional>], the command + injected DbContext, add an entity, and return an event that
/// Wolverine sends as a cascading message. Wolverine's EF Core middleware wraps this in the outbox
/// transaction (enroll -> SaveChanges -> commit), all of which the F# codegen now emits.
type CreateItemHandler =
    [<Transactional>]
    static member Handle(command: CreateItemCommand, db: ItemsDbContext) : ItemCreated =
        let item = { Id = Guid.NewGuid(); Name = command.Name }
        db.Items.Add(item) |> ignore
        { Id = item.Id }
