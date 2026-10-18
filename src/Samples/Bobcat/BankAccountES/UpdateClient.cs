using FluentValidation;
using Wolverine.Http;
using Wolverine.Persistence.EventSourcing;

namespace BankAccountES;

public record UpdateClient(Guid ClientId, string Name, string Email)
{
    public class Validator : AbstractValidator<UpdateClient>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Email).NotEmpty().EmailAddress();
        }
    }
}

public static class UpdateClientEndpoint
{
    // [DeciderFunction] is the store-agnostic spelling of Wolverine.Marten's [AggregateHandler]
    // (which now derives from it): load the Client stream, hand the current state in, append what
    // comes back. Same attribute, whichever store Program.cs registered.
    // RouteName is what a spec can address the endpoint by instead of its URL (bobcat#500): the Bobcat
    // generator resolves it to this template at compile time, from this assembly's metadata.
    [WolverinePut("/api/clients/{clientId}", RouteName = "UpdateClient")]
    [DeciderFunction]
    public static (IResult, ClientUpdated) Put(UpdateClient command, Client client)
    {
        return (Results.NoContent(), new ClientUpdated(command.ClientId, command.Name, command.Email));
    }
}
