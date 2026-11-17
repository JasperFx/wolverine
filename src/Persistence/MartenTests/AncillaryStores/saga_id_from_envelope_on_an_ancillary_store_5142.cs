using IntegrationTests;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Tracking;

namespace MartenTests.AncillaryStores;

/// <summary>
///     GH-5142, the last chain-less id-type question. A saga message with no identity member of its own gets its
///     saga id from the envelope, and the generated code parses it to the saga's id type. That type was asked of
///     the DEFAULT store, so a saga keyed by a string <c>Code</c> on the ancillary store it lives on was parsed as
///     a Guid. Same fix shape as GH-4441: ask the store the chain is routed to.
/// </summary>
public class saga_id_from_envelope_on_an_ancillary_store_5142 : IAsyncLifetime
{
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.MessageStorageSchemaName = "wolverine";
                opts.Durability.Mode = DurabilityMode.Solo;

                // Knows nothing about CodedSaga, so it answers with the conventional Guid Id
                opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "saga_envelope_id_main";
                }).IntegrateWithWolverine();

                opts.Services.AddMartenStore<ICodedSagaStore>(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "saga_envelope_id_sagas";
                    m.Schema.For<CodedSaga>().Identity(x => x.Code);
                }).IntegrateWithWolverine();

                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(CodedSaga));

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    [Fact]
    public void the_envelope_saga_id_is_read_as_the_ancillary_stores_id_type()
    {
        var runtime = theHost.GetRuntime();
        runtime.Handlers.HandlerFor<TouchCodedSaga>();
        var code = runtime.Handlers.ChainFor<TouchCodedSaga>().ShouldNotBeNull().SourceCode.ShouldNotBeNull();

        // string: read as is. Guid would have been "System.Guid.TryParse(envelope.SagaId, ...)"
        code.ShouldContain(".SagaId;");
        code.ShouldNotContain("System.Guid.TryParse");
    }
}

public interface ICodedSagaStore : IDocumentStore;

public record StartCodedSaga(string Code);

// No identity member, so the saga id comes off the envelope
public record TouchCodedSaga;

[MartenStore(typeof(ICodedSagaStore))]
public class CodedSaga : Wolverine.Saga
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public int Touches { get; set; }

    public static CodedSaga Start(StartCodedSaga command) => new() { Code = command.Code };

    public void Handle(TouchCodedSaga _) => Touches++;
}
