using FluentValidation;
using Marten;
using Wolverine.Http;
using Wolverine.Persistence;

namespace Catalog;

public record UpdateProduct(Guid Id, string Name, List<string> Category, string Description, string ImageFile, decimal Price)
{
    public class Validator : AbstractValidator<UpdateProduct>
    {
        public Validator()
        {
            RuleFor(x => x.Id).NotEmpty();
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Price).GreaterThan(0);
        }
    }
}

public static class UpdateProductEndpoint
{
    [WolverinePut("/products")]
    public static Product Put(UpdateProduct command, [Entity(Required = true)] Product product, IDocumentSession session)
    {
        product.Name = command.Name;
        product.Category = command.Category;
        product.Description = command.Description;
        product.ImageFile = command.ImageFile;
        product.Price = command.Price;

        session.Store(product);
        return product;
    }
}
