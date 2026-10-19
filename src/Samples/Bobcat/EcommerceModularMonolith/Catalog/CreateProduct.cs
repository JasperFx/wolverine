using FluentValidation;
using Marten;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;

namespace Catalog;

public record CreateProduct(string Name, List<string> Category, string Description, string ImageFile, decimal Price)
{
    public class Validator : AbstractValidator<CreateProduct>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Category).NotEmpty();
            RuleFor(x => x.Price).GreaterThan(0);
        }
    }
}

public static class CreateProductEndpoint
{
    // Created rather than a bare Product, so the response carries the 201 and Location a
    // newly-created resource should. Returned as a TypedResults value directly — a
    // (Product, IResult) tuple would be read by Wolverine.HTTP as (body, cascaded-message)
    // and the IResult dispatched as a message with no handler. See docs/sample-wiring.md
    // footgun 3.
    [WolverinePost("/products")]
    public static Created<Product> Post(CreateProduct command, IDocumentSession session)
    {
        var product = new Product
        {
            Id = Guid.NewGuid(),
            Name = command.Name,
            Category = command.Category,
            Description = command.Description,
            ImageFile = command.ImageFile,
            Price = command.Price,
        };

        session.Store(product);
        return TypedResults.Created($"/products/{product.Id}", product);
    }
}
