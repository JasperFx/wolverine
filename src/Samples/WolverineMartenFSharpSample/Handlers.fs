namespace WolverineMartenFSharpSample

open System
open Marten
open Wolverine.Attributes

/// A Marten document transactional message handler written in F#. [<Transactional>] + the command +
/// an injected IDocumentSession: store a document and return an event that Wolverine sends as a
/// cascading message. Wolverine's Marten middleware opens the outbox-enrolled session and saves
/// changes around this call — both of which the F# codegen now emits.
// begin-snippet: sample_fsharp_marten_document_handler
type CreateProductHandler =
    [<Transactional>]
    static member Handle(command: CreateProductCommand, session: IDocumentSession) : ProductCreated =
        let product = { Id = Guid.NewGuid(); Name = command.Name }
        session.Store<Product>(product)
        { Id = product.Id }
// end-snippet

/// Renames a Product. F# cannot apply a generic attribute, so [DocumentExists<Product>] cannot sit
/// on Handle here; the F# codegen sample (Wolverine.Marten.FSharpTests) applies it to this chain from
/// a handler policy instead, which exercises the existence check's F# emitter.
type RenameProductHandler =
    static member Handle(command: RenameProductCommand, session: IDocumentSession) : unit =
        session.Store<Product>({ Id = command.ProductId; Name = command.Name })
