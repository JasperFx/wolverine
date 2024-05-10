using Alba;
using Marten.Schema.Identity;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class compiled_query_writer : IntegrationContext
{
    public compiled_query_writer(AppFixture fixture) : base(fixture)
    {
    }

    [Fact]
    public async Task endpoint_returning_compiled_list_query_should_return_query_result()
    {
        await using var session = Store.LightweightSession();
        int notApprovedInvoices = 5;
        int approvedInvoices = 3;
        for (int i = 0; i < notApprovedInvoices; i++)
        {
            var invoice =
                new Invoice()
                {
                    Approved = false
                };
            session.Store(invoice);
        }

        for (int i = 0; i < approvedInvoices; i++)
        {
            var invoice =
                new Invoice()
                {
                    Approved = true
                };
            session.Store(invoice);
        }

        await session.SaveChangesAsync();

        var approvedInvoiceList = await Host.GetAsJson<List<Invoice>>("/invoices/approved");
        approvedInvoiceList.ShouldNotBeNull();
        approvedInvoiceList.Count.ShouldBe(approvedInvoices);
    }

    [Fact]
    public async Task endpoint_returning_compiled_primitive_query_should_return_query_result()
    {
        await using var session = Store.LightweightSession();
        int invoicesCount = 5;
        for (int i = 0; i < invoicesCount; i++)
        {
            var invoice =
                new Invoice()
                {
                };
            session.Store(invoice);
        }

        await session.SaveChangesAsync();

        var invoiceCountString = await Host.GetAsText("/invoices/compiled/count");
        invoiceCountString.ShouldNotBeNull();
        int.TryParse(invoiceCountString, out var result).ShouldBeTrue();
        result.ShouldBe(invoicesCount);
    }

    // [Fact]
    // public async Task endpoint_returning_compiled_primitive_query_should_return_query_result_for_string()
    // {
    //     if (DateTime.Today > new DateTime(2024, 2, 28)) throw new Exception("JEREMY NEEDS TO FIX THIS");
    //     return;
    //     
    //     await using var session = Store.LightweightSession();
    //     int invoicesCount = 5;
    //     Guid id = Guid.Empty;
    //     for (int i = 0; i < invoicesCount; i++)
    //     {
    //         var invoice =
    //             new Invoice()
    //             {
    //             };
    //         session.Store(invoice);
    //         id = invoice.Id;
    //     }
    //     id.ShouldNotBe(Guid.Empty);


    // [Fact]
    // public async Task endpoint_returning_compiled_primitive_query_should_return_query_result_for_string()
    // {
    //     if (DateTime.Today > new DateTime(2024, 2, 28)) throw new Exception("JEREMY NEEDS TO FIX THIS");
    //     return;
    //     
    //     await using var session = Store.LightweightSession();
    //     int invoicesCount = 5;
    //     Guid id = Guid.Empty;
    //     for (int i = 0; i < invoicesCount; i++)
    //     {
    //         var invoice =
    //             new Invoice()
    //             {
    //             };
    //         session.Store(invoice);
    //         id = invoice.Id;
    //     }
    //     id.ShouldNotBe(Guid.Empty);

    // TODO -- come back to this soon.
    // [Fact]
    // public async Task endpoint_returning_compiled_primitive_query_should_return_query_result_for_string()
    // {
    //     await using var session = Store.LightweightSession();
    //     int invoicesCount = 5;
    //     Guid id = Guid.Empty;
    //     for (int i = 0; i < invoicesCount; i++)
    //     {
    //         var invoice =
    //             new Invoice()
    //             {
    //             };
    //         session.Store(invoice);
    //         id = invoice.Id;
    //     }
    //     id.ShouldNotBe(Guid.Empty);
    //
    //     await session.SaveChangesAsync();
    //
    //     var invoiceId = await Host.GetAsText($"/invoices/compiled/string/{id}");
    //     invoiceId.ShouldNotBeNull();
    //     invoiceId.ShouldBe(id.ToString());
    // }

    [Fact]
    public async Task endpoint_returning_compiled_query_should_return_query_result()
    {
        var invoice = new Invoice()
        {
            Id = Guid.NewGuid()
        };
        using var session = Store.LightweightSession();
        session.Store(invoice);
        await session.SaveChangesAsync();


        var invoiceCompiled = await Host.GetAsJson<Invoice>($"/invoices/compiled/{invoice.Id}");
        invoiceCompiled.ShouldNotBeNull();
        invoiceCompiled.Id.ShouldBe(invoice.Id);

        await Host.Scenario(x =>
        {
            x.Get.Url($"/invoices/compiled/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });
    }
}