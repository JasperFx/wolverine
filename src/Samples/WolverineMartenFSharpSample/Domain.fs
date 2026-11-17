namespace WolverineMartenFSharpSample

open System

/// The Marten document persisted by the sample. Marten identifies documents by an `Id` member;
/// [<CLIMutable>] gives the record the parameterless constructor + settable properties Marten needs.
[<CLIMutable>]
type Product = { Id: Guid; Name: string }

/// The command handled by CreateProductHandler.
type CreateProductCommand = { Name: string }

/// The event cascaded out after the product is stored.
type ProductCreated = { Id: Guid }

/// The command handled by RenameProductHandler. Its ProductId names the Product that
/// [DocumentExists<Product>] requires to exist before the handler runs.
type RenameProductCommand = { ProductId: Guid; Name: string }
