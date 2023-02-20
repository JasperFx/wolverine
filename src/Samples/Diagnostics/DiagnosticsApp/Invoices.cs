using Wolverine;
using Wolverine.Attributes;

namespace IntegrationTests;

public record CreateInvoice(string Name);

public record InvoiceCreated(Guid Id, string Name);

public static class CreateInvoiceHandler
{
    public static InvoiceCreated Handle(CreateInvoice command)
    {
        Guid id = Guid.NewGuid();
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
}

public record InvoiceShipped(Guid Id) : IEvent;
public record CreateShippingLabel(Guid Id) : ICommand;

[WolverineMessage]
public record AddItem(Guid Id, string ItemName);