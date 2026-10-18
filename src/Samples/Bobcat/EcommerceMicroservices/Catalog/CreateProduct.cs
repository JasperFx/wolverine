using FluentValidation;
using Marten;
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
    [WolverinePost("/products")]
    public static Product Post(CreateProduct command, IDocumentSession session)
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
        return product;
    }
}
