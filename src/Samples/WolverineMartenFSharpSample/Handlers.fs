namespace WolverineMartenFSharpSample

open System
open Marten
open Wolverine.Attributes

/// A Marten document transactional message handler written in F#. [<Transactional>] + the command +
/// an injected IDocumentSession: store a document and return an event that Wolverine sends as a
/// cascading message. Wolverine's Marten middleware opens the outbox-enrolled session and saves
/// changes around this call — both of which the F# codegen now emits.
type CreateProductHandler =
    [<Transactional>]
    static member Handle(command: CreateProductCommand, session: IDocumentSession) : ProductCreated =
        let product = { Id = Guid.NewGuid(); Name = command.Name }
        session.Store<Product>(product)
        { Id = product.Id }
