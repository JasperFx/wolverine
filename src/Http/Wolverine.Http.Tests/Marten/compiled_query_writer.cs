using Alba;
using Shouldly;
using WolverineWebApi.Marten;

namespace Wolverine.Http.Tests.Marten;

public class compiled_query_writer : IntegrationContext
{
    public compiled_query_writer(AppFixture fixture) : base(fixture)
    {
    }

    // Warm the endpoint through the real request pipeline so the chain's handler is
    // compiled by the same path production uses, then hand back the generated source.
    // Calling ICodeFile.InitializeSynchronously here instead would pre-empt the
    // route's own lazy buildHandler() and leave the shared AppFixture chain unable
    // to serve requests in later tests.
    private async Task<string> sourceCodeFor(string url, int expectedStatusCode = 200)
    {
        await Host.Scenario(x =>
        {
            x.Get.Url(url);
            x.StatusCodeShouldBe(expectedStatusCode);
        });

        var chain = HttpChains.ChainFor("GET", url);
        chain.ShouldNotBeNull();

        var source = chain.SourceCode;
        source.ShouldNotBeNull("Failed to generate the source code");
        return source;
    }

    // GH-3625: QuerySpecificationPolicy saw the compiled query variable created by
    // the endpoint method and injected a redundant QueryAsync() fetch whose result
    // was discarded, so every request executed the query twice — once for the
    // discarded fetch, once for CompiledQueryWriterPolicy's streaming call.

    [Fact]
    public async Task compiled_list_query_resource_is_streamed_without_a_redundant_fetch()
    {
        var source = await sourceCodeFor("/invoices/approved");

        source.ShouldContain("WriteArray");
        source.ShouldNotContain("QueryAsync",
            customMessage: $"The chain's resource is streamed by WriteArray(); a QueryAsync() call means the query also ran a second, discarded time. Source code follows:\n{source}");
    }

    [Fact]
    public async Task compiled_single_query_resource_is_streamed_without_a_redundant_fetch()
    {
        var chain = HttpChains.ChainFor("GET", "/invoices/compiled/{id}");
        chain.ShouldNotBeNull();

        // 404 is the WriteOne path for an unknown id — still compiles and runs the chain
        await Host.Scenario(x =>
        {
            x.Get.Url($"/invoices/compiled/{Guid.NewGuid()}");
            x.StatusCodeShouldBe(404);
        });

        var source = chain.SourceCode;
        source.ShouldNotBeNull("Failed to generate the source code");

        source.ShouldContain("WriteOne");
        source.ShouldNotContain("QueryAsync",
            customMessage: $"The chain's resource is streamed by WriteOne(); a QueryAsync() call means the query also ran a second, discarded time. Source code follows:\n{source}");
    }

    [Fact]
    public async Task compiled_primitive_query_resource_is_executed_exactly_once()
    {
        var source = await sourceCodeFor("/invoices/compiled/count");

        // Match the call site rather than the bare name, which also appears in the
        // result variable's name
        var count = source.Split(".QueryAsync<").Length - 1;
        count.ShouldBe(1,
            $"Expected exactly one QueryAsync() call in the generated source, found {count}. Source code follows:\n{source}");
    }

    [Fact]
    public async Task load_returned_compiled_query_dependency_keeps_its_fetch()
    {
        var source = await sourceCodeFor("/invoices/approved/count");

        // Here the compiled query is a data dependency produced by Load(), not the
        // chain's resource, so the FetchSpecificationFrame must still execute it.
        source.ShouldContain("QueryAsync",
            customMessage: $"The Load-returned compiled query must still be fetched for the endpoint method. Source code follows:\n{source}");

        // And the fetch result actually feeds the endpoint method at runtime
        var text = await Host.GetAsText("/invoices/approved/count");
        int.TryParse(text, out _).ShouldBeTrue($"Expected an integer count in the response, got '{text}'");
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

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

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

        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

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
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);


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