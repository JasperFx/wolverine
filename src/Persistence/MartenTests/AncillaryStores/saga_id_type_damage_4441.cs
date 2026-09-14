using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Persistence;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
///     GH-4441, the damage half. <c>saga_id_type_across_stores_4441</c> proves the two stores can disagree
///     about an aggregate's id type; this asks whether that disagreement is <b>reachable from a handler</b>,
///     which is what decides between "bug" and "close with a comment".
/// </summary>
/// <remarks>
///     <para>
///     The shape: the aggregate is identified by <c>Code</c> (a string) on the ancillary store it actually
///     lives on, the chain is routed there with <c>[MartenStore]</c>, and the command carries only that code.
///     <c>DetermineSagaIdType</c> asks the DEFAULT store, which has never heard of the type, and gets the
///     conventional <c>Guid Id</c> back — so identity resolution goes looking for a Guid member on a command
///     that has none.
///     </para>
///     <para>
///     If this is red, GH-4441 is a real bug with the GH-4439 fix shape (consult the chain's store). If it is
///     green, the divergence the probe demonstrates is unreachable through this path and the issue should say
///     so rather than carry a speculative fix.
///     </para>
/// </remarks>
public class saga_id_type_damage_4441 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Policies.AutoApplyTransactions();

                // Knows nothing about CodedOrder. It will still answer "Guid" when asked for its id type.
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "saga_id_damage_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<ICodedOrderStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "saga_id_damage_orders";

                    // The identity that actually applies to this document, on the store that holds it.
                    m.Schema.For<CodedOrder>().Identity(x => x.Code);
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(CodedOrderHandler));

                opts.Services.AddResourceSetupOnStartup();
                opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public async Task entity_on_an_ancillary_store_resolves_its_identity_through_that_store()
    {
        var code = "ORD-" + Guid.NewGuid().ToString("N");

        var store = theHost.DocumentStore<ICodedOrderStore>();
        await using (var session = store.LightweightSession())
        {
            session.Store(new CodedOrder { Code = code, Total = 10m });
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.TrackActivity().SendMessageAndWaitAsync(new AddToCodedOrder(code, 5m));

        await using var verify = store.LightweightSession();
        var order = await verify.LoadAsync<CodedOrder>(code, TestContext.Current.CancellationToken);

        order.ShouldNotBeNull();
        order.Total.ShouldBe(15m);
    }
}

public interface ICodedOrderStore : IDocumentStore;

public class CodedOrder
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public decimal Total { get; set; }
}

public record AddToCodedOrder(string Code, decimal Amount);

[MartenStore(typeof(ICodedOrderStore))]
public static class CodedOrderHandler
{
    public static void Handle(AddToCodedOrder command, [Entity("Code")] CodedOrder order,
        IDocumentSession session)
    {
        order.Total += command.Amount;
        session.Store(order);
    }
}
