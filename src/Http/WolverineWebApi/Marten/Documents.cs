using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

public class InvoicesEndpoint
{
    [WolverineGet("/invoices/{id}")]
    public static Invoice Get([Document] Invoice invoice)
    {
        return invoice;
    }

    [WolverinePost("/invoices/{invoiceId}/pay")]
    public static IMartenOp Pay([Document] Invoice invoice)
    {
        invoice.Paid = true;
        return MartenOps.Store(invoice);
    }
    
    [WolverinePost("/invoices/{number}/approve")]
    public static IMartenOp Approve([Document("number")] Invoice invoice)
    {
        invoice.Approved = true;
        return MartenOps.Store(invoice);
    }
}

public class Invoice
{
    public Guid Id { get; set; }
    public bool Paid { get; set; }
    public bool Approved { get; set; }
}