using Wolverine.Http;
using Wolverine.Http.Marten;
using WolverineWebApi.Marten;

namespace WolverineWebApi.Bugs;

// GH-2205: Endpoints with multiple [Document] or [Aggregate] args
// should generate valid code with all route values extracted before
// the batch query

public class Receipt
{
    public Guid Id { get; set; }
    public decimal Amount { get; set; }
}

public static class Bug2205_MultipleDocumentEndpoint
{
    [WolverineGet("/bug2205/documents/{invoiceId}/{receiptId}")]
    public static string GetWithMultipleDocuments(
        [Document("invoiceId")] Invoice invoice,
        [Document("receiptId")] Receipt receipt)
    {
        return $"{invoice.Id}-{receipt.Id}";
    }

    [WolverineGet("/bug2205/document-and-aggregate/{invoiceId}/{orderId}")]
    public static string GetWithDocumentAndAggregate(
        [Document("invoiceId")] Invoice invoice,
        [Aggregate("orderId")] Order order)
    {
        return $"{invoice.Id}-{order.Id}";
    }
}
