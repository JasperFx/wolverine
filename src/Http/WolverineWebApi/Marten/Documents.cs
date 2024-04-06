using System.Linq.Expressions;
using Marten;
using Marten.Linq;
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

    #region sample_using_marten_op_from_http_endpoint

    [WolverinePost("/invoices/{invoiceId}/pay")]
    public static IMartenOp Pay([Document] Invoice invoice)
    {
        invoice.Paid = true;
        return MartenOps.Store(invoice);
    }

    #endregion

    #region sample_overriding_route_argument_with_document_attribute

    [WolverinePost("/invoices/{number}/approve")]
    public static IMartenOp Approve([Document("number")] Invoice invoice)
    {
        invoice.Approved = true;
        return MartenOps.Store(invoice);
    }

    #endregion

    #region sample_compiled_query_return_endpoint
    [WolverineGet("/invoices/approved")]
    public static ApprovedInvoicedCompiledQuery GetApproved()
    {
        return new ApprovedInvoicedCompiledQuery();
    } 
    #endregion
    
    [WolverineGet("/invoices/compiled/{id}")]
    public static ByIdCompiled GetCompiled(Guid id)
    {
        return new ByIdCompiled(id);
    } 
    
    [WolverineGet("/invoices/compiled/count")]
    public static CompiledCountQuery GetCompiledCount()
    {
        return new CompiledCountQuery();
    } 
    
    [WolverineGet("/invoices/compiled/string/{id}")]
    public static CompiledStringQuery GetCompiledString(Guid id)
    {
        return new CompiledStringQuery(id);
    } 
}

public class Invoice
{
    public Guid Id { get; set; }
    public bool Paid { get; set; }
    public bool Approved { get; set; }
}

#region sample_compiled_query_return_query

public class ApprovedInvoicedCompiledQuery : ICompiledListQuery<Invoice>
{
    public Expression<Func<IMartenQueryable<Invoice>, IEnumerable<Invoice>>> QueryIs()
    {
        return q => q.Where(x => x.Approved);
    }
}

#endregion

public class ByIdCompiled : ICompiledQuery<Invoice, Invoice?>
{
    public readonly Guid Id;

    public ByIdCompiled(Guid id)
    {
        Id = id;
    }
    
    public Expression<Func<IMartenQueryable<Invoice>, Invoice?>> QueryIs()
    {
        return q => q.FirstOrDefault(x => x.Id == Id);
    }
}

public class CompiledCountQuery : ICompiledQuery<Invoice, int>
{
    public Expression<Func<IMartenQueryable<Invoice>, int>> QueryIs()
    {
        return q => q.Count();
    }
}
public class CompiledStringQuery : ICompiledQuery<Invoice, string>
{
    public readonly Guid Id;

    public CompiledStringQuery(Guid id)
    {
        Id = id;
    }
    public Expression<Func<IMartenQueryable<Invoice>, string>> QueryIs()
    {
        return q => q.Where(x => x.Id == Id).Select(x => x.Id.ToString()).First();
    }
}
