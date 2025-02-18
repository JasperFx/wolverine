using Wolverine;
using Wolverine.Attributes;
using Wolverine.Configuration;
using Wolverine.ErrorHandling;
using Wolverine.Runtime.Handlers;

namespace IntegrationTests;

public record CreateInvoice(string Name);

public record InvoiceCreated(Guid Id, string Name);

public static class CreateInvoiceHandler
{
    public static InvoiceCreated Handle(CreateInvoice command)
    {
        var id = Guid.NewGuid();
        return new InvoiceCreated(id, command.Name);
    }
}

public record AssignUser(Guid UserId, Guid InvoiceId);

public record OrderParts;

public record StartInvoiceProcessing(Guid Id);

public static class StartInvoiceProcessingHandler
{
    public static Task<(AssignUser, OrderParts)> Handle(StartInvoiceProcessing command)
    {
        var returnValue = (new AssignUser(Guid.NewGuid(), command.Id), new OrderParts());
        return Task.FromResult(returnValue);
    }

    public static void Configure(HandlerChain chain)
    {
        chain.OnAnyException().Requeue();
    }
}

// These are all published messages that aren't
// obvious to Wolverine from message handler endpoint
// signatures
public record InvoiceShipped(Guid Id) : IEvent;

public record CreateShippingLabel(Guid Id) : ICommand;

[WolverineMessage]
public record AddItem(Guid Id, string ItemName);

// Just need a fake type here for discovery
public record PublishedMessage;