using FluentValidation;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine.Http;
using Wolverine.Persistence;

namespace BankAccountES;

public record EnrollClient(string Name, string Email)
{
    public class Validator : AbstractValidator<EnrollClient>
    {
        public Validator()
        {
            RuleFor(x => x.Name).NotEmpty();
            RuleFor(x => x.Email).NotEmpty().EmailAddress();
        }
    }
}

public static class EnrollClientEndpoint
{
    // Created rather than a bare Client, so the response carries the 201 and Location a
    // newly-created resource should. The second tuple member is a Wolverine side effect, not a
    // cascaded message — Storage.StartStream() is how a handler starts a stream without naming the
    // store. (A (Client, IResult) tuple, by contrast, would be read as (body, cascaded-message) and
    // the IResult dispatched as a message with no handler. See docs/sample-wiring.md footgun 3.)
    [WolverinePost("/api/clients")]
    public static (Created<Client>, StartStream) Post(EnrollClient command)
    {
        var clientId = Guid.NewGuid();
        var evt = new ClientEnrolled(clientId, command.Name, command.Email);

        var client = new Client();
        client.Apply(evt);
        return (
            TypedResults.Created($"/api/clients/{clientId}", client),
            Storage.StartStream<Client>(clientId, evt));
    }
}
