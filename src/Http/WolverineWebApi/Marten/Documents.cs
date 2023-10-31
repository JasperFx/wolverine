using Marten;
using Microsoft.AspNetCore.Mvc;
using Wolverine.Http;
using Wolverine.Http.Marten;
using Wolverine.Marten;

namespace WolverineWebApi.Marten;

public class InvoicesEndpoint

    #region sample_get_invoice_longhand

{
    [WolverineGet("/invoices/longhand/id")]
    [ProducesResponseType(404)] 
    [ProducesResponseType(200, Type = typeof(Invoice))]
    public static async Task<IResult> GetInvoice(
        Guid id, 
        IQuerySession session, 
        CancellationToken cancellationToken)
    {
        var invoice = await session.LoadAsync<Invoice>(id, cancellationToken);
        if (invoice == null) return Results.NotFound();

        return Results.Ok(invoice);
    }

    #endregion

    #region sample_using_document_attribute

    [WolverineGet("/invoices/{id}")]
    public static Invoice Get([Document] Invoice invoice)
    {
        return invoice;
    }

    #endregion

    [WolverinePost("/invoices/{invoiceId}/pay")]
    public static IMartenOp Pay([Document] Invoice invoice)
    {
        invoice.Paid = true;
        return MartenOps.Store(invoice);
    }

    #region sample_overriding_route_argument_with_document_attribute

    [WolverinePost("/invoices/{number}/approve")]
    public static IMartenOp Approve([Document("number")] Invoice invoice)
    {
        invoice.Approved = true;
        return MartenOps.Store(invoice);
    }

    #endregion
}

public class Invoice
{
    public Guid Id { get; set; }
    public bool Paid { get; set; }
    public bool Approved { get; set; }
}