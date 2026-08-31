using Wolverine.Persistence;

namespace Wolverine.AzureBlobStorage.Tests;

public record InvoiceContent(string Id, string Body);

public record ReadInvoice(string Id);

public record TouchInvoice(string Id);

public record ReadOptionalInvoice(string Id);

public record WriteInvoice(string Id, string Body);

public record DeleteInvoice(string Id);

public record ReplaceInvoices(string Id, string Body, string OtherId);

public static class InvoiceHandler
{
    /// <summary>
    /// Records every invoice a handler actually saw, so a test can tell "the handler ran and the body
    /// was X" apart from "the handler never ran because the blob was missing".
    /// </summary>
    public static readonly List<string> Touched = [];

    public static string Handle(ReadInvoice command, [Entity] InvoiceContent content)
    {
        return content.Body;
    }

    public static void Handle(TouchInvoice command, [Entity] InvoiceContent content)
    {
        Touched.Add(content.Body);
    }

    public static string? Handle(ReadOptionalInvoice command,
        [Entity(Required = false)] InvoiceContent? content)
    {
        return content?.Body;
    }

    public static IStorageAction<InvoiceContent> Handle(WriteInvoice command)
    {
        return Storage.Store(new InvoiceContent(command.Id, command.Body));
    }

    public static IStorageAction<InvoiceContent> Handle(DeleteInvoice command, [Entity] InvoiceContent content)
    {
        return Storage.Delete(content);
    }

    public static UnitOfWork<InvoiceContent> Handle(ReplaceInvoices command)
    {
        return new UnitOfWork<InvoiceContent>()
            .Store(new InvoiceContent(command.Id, command.Body))
            .Delete(new InvoiceContent(command.OtherId, string.Empty));
    }
}

/// <summary>
/// The blob name layout these tests register: a generation prefix, the tenant when there is one, and
/// the id. Deliberately not something Wolverine could have guessed.
/// </summary>
public static class InvoiceNames
{
    public const string Container = "wolverine-blob-persistence-tests";

    public static string For(BlobNameContext ctx)
    {
        return ctx.TenantId == null
            ? $"invoices/v7/shared/{ctx.Id}.json"
            : $"invoices/v7/{ctx.TenantId}/{ctx.Id}.json";
    }
}
