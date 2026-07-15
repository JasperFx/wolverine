using System.Text.Json.Serialization;
using JasperFx;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Attributes;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Sagas;
using Wolverine.Runtime.Handlers;
using JsonPropertyAttribute = Newtonsoft.Json.JsonPropertyAttribute;

namespace CosmosDbTests;

/// <summary>
/// GH-3416. CosmosDB requires a lowercase "id" on every document, so a saga with a PascalCase Id only
/// round trips if the CosmosClient was told to camel case its property names. These tests pin the startup
/// guard that says so, and they deliberately need no emulator: a CosmosClient does no I/O until it is asked
/// to, and the guard only serializes a probe saga with the client's own serializer.
/// </summary>
public class saga_serialization_validation
{
    // The well known CosmosDB emulator key. Never connected to -- CosmosClient does no I/O in its constructor
    private const string ConnectionString =
        "AccountEndpoint=https://localhost:8081/;AccountKey=C2y6yDjf5/R+ob0N8A7Cgv30VRDJIWEHLM+4QDU5DE2nQ9nDuVTqobD4b8mGGyPMbIZnqyMsEcaGQy67XIw/Jw==";

    private static CosmosClient clientWith(CosmosPropertyNamingPolicy? policy)
    {
        var options = new CosmosClientOptions();
        if (policy.HasValue)
        {
            options.SerializerOptions = new CosmosSerializationOptions { PropertyNamingPolicy = policy.Value };
        }

        return new CosmosClient(ConnectionString, options);
    }

    private static CosmosSerializer serializerFor(CosmosPropertyNamingPolicy? policy)
    {
        return clientWith(policy).ClientOptions.Serializer;
    }

    [Fact]
    public void pascal_case_id_does_not_write_a_cosmos_id_without_camel_casing()
    {
        var serializer = serializerFor(null);

        CosmosSagaIdentity.DetermineJsonPropertyName(typeof(PascalCaseSaga), serializer).ShouldBe("Id");
        CosmosSagaIdentity.WillWriteCosmosId(typeof(PascalCaseSaga), serializer).ShouldBeFalse();
    }

    [Fact]
    public void pascal_case_id_is_fine_when_the_client_camel_cases()
    {
        var serializer = serializerFor(CosmosPropertyNamingPolicy.CamelCase);

        CosmosSagaIdentity.DetermineJsonPropertyName(typeof(PascalCaseSaga), serializer).ShouldBe("id");
        CosmosSagaIdentity.WillWriteCosmosId(typeof(PascalCaseSaga), serializer).ShouldBeTrue();
    }

    [Fact]
    public void an_explicit_newtonsoft_mapping_is_enough_on_its_own()
    {
        CosmosSagaIdentity.WillWriteCosmosId(typeof(NewtonsoftMappedSaga), serializerFor(null)).ShouldBeTrue();
    }

    /// <summary>
    /// The SDK's default serializer is Newtonsoft based, so a System.Text.Json mapping does NOT rescue a
    /// saga that would otherwise be written as "Id" -- exactly the sort of thing a user is better off
    /// hearing at startup than inferring from a 400
    /// </summary>
    [Fact]
    public void a_system_text_json_mapping_does_not_help_the_default_newtonsoft_serializer()
    {
        CosmosSagaIdentity.DetermineJsonPropertyName(typeof(SystemTextJsonMappedSaga), serializerFor(null))
            .ShouldBe("Id");
    }

    /// <summary>
    /// Camel casing does not save a saga whose identity member is not named "Id" at all -- "PickupSagaId"
    /// camel cases to "pickupSagaId", and the document still has no id
    /// </summary>
    [Fact]
    public void a_differently_named_identity_member_still_has_to_be_mapped()
    {
        var serializer = serializerFor(CosmosPropertyNamingPolicy.CamelCase);

        CosmosSagaIdentity.DetermineJsonPropertyName(typeof(PickupSaga), serializer).ShouldBe("pickupSagaId");
        CosmosSagaIdentity.WillWriteCosmosId(typeof(PickupSaga), serializer).ShouldBeFalse();
    }

    [Fact]
    public async Task throws_at_startup_when_the_client_will_not_write_a_document_id()
    {
        using var host = await hostWith(typeof(PascalCaseSaga));

        var ex = await Should.ThrowAsync<InvalidOperationException>(() =>
            validatorFor(host, clientWith(null)).StartAsync(CancellationToken.None));

        ex.Message.ShouldContain(nameof(PascalCaseSaga));
        ex.Message.ShouldContain("CosmosPropertyNamingPolicy.CamelCase");
    }

    [Fact]
    public async Task passes_when_the_client_camel_cases_its_property_names()
    {
        using var host = await hostWith(typeof(PascalCaseSaga));

        await Should.NotThrowAsync(() =>
            validatorFor(host, clientWith(CosmosPropertyNamingPolicy.CamelCase)).StartAsync(CancellationToken.None));
    }

    /// <summary>
    /// A saga that maps its own identity member to "id" is already correct under any naming policy, and
    /// refusing to start that application would be a false alarm
    /// </summary>
    [Fact]
    public async Task passes_when_the_saga_maps_its_own_document_id()
    {
        using var host = await hostWith(typeof(NewtonsoftMappedSaga));

        await Should.NotThrowAsync(() =>
            validatorFor(host, clientWith(null)).StartAsync(CancellationToken.None));
    }

    /// <summary>
    /// An application using CosmosDB purely for envelope storage has nothing to fix -- every document
    /// Wolverine writes itself already carries an explicit "id" mapping -- so it has to keep booting
    /// </summary>
    [Fact]
    public async Task passes_when_there_are_no_sagas_at_all()
    {
        using var host = await hostWith(null);

        await Should.NotThrowAsync(() =>
            validatorFor(host, clientWith(null)).StartAsync(CancellationToken.None));
    }

    /// <summary>
    /// A custom CosmosSerializer is entitled to reject a type it was never meant to see, and the guard is in
    /// no position to second guess it
    /// </summary>
    [Fact]
    public async Task stands_down_for_a_serializer_that_will_not_take_the_saga()
    {
        using var host = await hostWith(typeof(PascalCaseSaga));

        var client = new CosmosClient(ConnectionString,
            new CosmosClientOptions { Serializer = new FakeCosmosSerializer() });

        await Should.NotThrowAsync(() => validatorFor(host, client).StartAsync(CancellationToken.None));
    }

    private static CosmosDbSagaSerializationValidator validatorFor(IHost host, CosmosClient client)
    {
        return new CosmosDbSagaSerializationValidator(
            client,
            host.Services.GetRequiredService<HandlerGraph>(),
            host.Services.GetRequiredService<WolverineOptions>(),
            host.Services.GetRequiredService<IServiceContainer>());
    }

    /// <summary>
    /// Stands in for a real UseCosmosDbPersistence() application: same persistence strategy and the same
    /// registered Container, but no message store -- so the handler graph the validator reads is the one a
    /// Cosmos application really compiles, without an emulator having to be up to boot it
    /// </summary>
    private static Task<IHost> hostWith(Type? sagaType)
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                var client = clientWith(CosmosPropertyNamingPolicy.CamelCase);
                opts.Services.AddSingleton(client.GetDatabase("wolverine").GetContainer(DocumentTypes.ContainerName));

                opts.CodeGeneration.InsertFirstPersistenceStrategy<CosmosDbPersistenceFrameProvider>();
                opts.CodeGeneration.ReferenceAssembly(typeof(CosmosDbPersistenceFrameProvider).Assembly);

                opts.Discovery.DisableConventionalDiscovery();
                if (sagaType != null)
                {
                    opts.Discovery.IncludeType(sagaType);
                }
            })
            .StartAsync();
    }
}

// These sagas exist only to be fed to the guard. [WolverineIgnore] keeps them out of the conventional
// discovery that the emulator-backed tests in this assembly run through IncludeAssembly() -- PickupSaga in
// particular is deliberately mis-mapped, and the guard would rightly refuse to start those hosts. An
// explicit Discovery.IncludeType() still picks them up here.
public record StartWorkflow(string Id);

public record StartPickup(string PickupSagaId);

[WolverineIgnore]
public class PascalCaseSaga : Saga
{
    public string Id { get; set; } = string.Empty;

    public static PascalCaseSaga Start(StartWorkflow command) => new() { Id = command.Id };
}

[WolverineIgnore]
public class NewtonsoftMappedSaga : Saga
{
    [JsonProperty("id")]
    public string Id { get; set; } = string.Empty;

    public static NewtonsoftMappedSaga Start(StartWorkflow command) => new() { Id = command.Id };
}

[WolverineIgnore]
public class SystemTextJsonMappedSaga : Saga
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    public static SystemTextJsonMappedSaga Start(StartWorkflow command) => new() { Id = command.Id };
}

[WolverineIgnore]
public class PickupSaga : Saga
{
    public string PickupSagaId { get; set; } = string.Empty;

    public static PickupSaga Start(StartPickup command) => new() { PickupSagaId = command.PickupSagaId };
}

internal class FakeCosmosSerializer : CosmosSerializer
{
    public override T FromStream<T>(Stream stream) => throw new NotSupportedException();

    public override Stream ToStream<T>(T input) => throw new NotSupportedException();
}
