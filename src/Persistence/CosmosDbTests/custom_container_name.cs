using System.Net;
using JasperFx.Core.Reflection;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.Persistence.Durability;
using Wolverine.Runtime;

namespace CosmosDbTests;

/// <summary>
///     Everything a Wolverine node shares with its peers — the node registry, leadership, agent assignments and the
///     single durability agent — lives in one container. With the container name fixed at "wolverine", every
///     application pointed at the same database joined one cluster, and the durability agent then fired due
///     scheduled messages into whichever node held it, even a node of another application with no handler for them.
///     <see cref="CosmosDbConfiguration.UseContainer" /> lets each application keep its own container, and so its
///     own cluster, inside a shared database.
/// </summary>
[Collection("cosmosdb")]
public class custom_container_name
{
    private readonly AppFixture _fixture;

    public custom_container_name(AppFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public void the_default_container_is_still_wolverine()
    {
        new CosmosDbConfiguration().ContainerName.ShouldBe("wolverine");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has/slash")]
    [InlineData("has\\backslash")]
    [InlineData("has#hash")]
    [InlineData("has?question")]
    [InlineData("trailing ")]
    public void an_invalid_container_id_is_refused_when_configured(string containerName)
    {
        // Refused here rather than at the first write, where CosmosDB would answer with a 400
        Should.Throw<ArgumentException>(() => new CosmosDbConfiguration().UseContainer(containerName));
    }

    [Fact]
    public void a_container_id_longer_than_cosmos_allows_is_refused()
    {
        Should.Throw<ArgumentException>(() => new CosmosDbConfiguration().UseContainer(new string('a', 256)));
    }

    [Fact]
    public async Task the_configured_container_is_created_on_startup()
    {
        var containerName = uniqueContainerName();

        try
        {
            using var host = await buildHostAsync(containerName, DurabilityMode.Solo);

            var properties = await database().GetContainer(containerName)
                .ReadContainerAsync(cancellationToken: TestContext.Current.CancellationToken);
            properties.Resource.PartitionKeyPath.ShouldBe(DocumentTypes.PartitionKeyPath);
        }
        finally
        {
            await dropAsync(containerName);
        }
    }

    [Fact]
    public async Task sagas_are_kept_in_the_configured_container_and_not_the_default_one()
    {
        var containerName = uniqueContainerName();

        try
        {
            using var host = await buildHostAsync(containerName, DurabilityMode.Solo);

            var id = Guid.NewGuid().ToString();
            await host.MessageBus().InvokeAsync(new StartPartitioned(id), TestContext.Current.CancellationToken);

            (await existsAsync(database().GetContainer(containerName), id)).ShouldBeTrue();
            (await existsAsync(_fixture.Container, id)).ShouldBeFalse();
        }
        finally
        {
            await dropAsync(containerName);
        }
    }

    [Fact]
    public async Task the_store_describes_itself_by_its_container()
    {
        var containerName = uniqueContainerName();

        try
        {
            using var host = await buildHostAsync(containerName, DurabilityMode.Solo);

            var descriptor = host.Services.GetRequiredService<IMessageStore>().As<CosmosDbMessageStore>().Describe();
            descriptor.DatabaseName.ShouldBe(AppFixture.DatabaseName);
            descriptor.SchemaOrNamespace.ShouldBe(containerName);
        }
        finally
        {
            await dropAsync(containerName);
        }
    }

    /// <summary>
    ///     The point of the option: two applications in one database, each in its own container, register their
    ///     nodes apart — so neither can be elected leader over the other or be handed the other's durability agent
    /// </summary>
    [Fact]
    public async Task applications_in_separate_containers_form_separate_clusters()
    {
        var first = uniqueContainerName();
        var second = uniqueContainerName();

        try
        {
            using var firstHost = await buildHostAsync(first, DurabilityMode.Balanced, "FirstService");
            using var secondHost = await buildHostAsync(second, DurabilityMode.Balanced, "SecondService");

            var firstNodes = await waitForNodesAsync(first);
            var secondNodes = await waitForNodesAsync(second);

            firstNodes.ShouldBe([nodeIdOf(firstHost)]);
            secondNodes.ShouldBe([nodeIdOf(secondHost)]);
        }
        finally
        {
            await dropAsync(first);
            await dropAsync(second);
        }
    }

    private Database database() => _fixture.Client.GetDatabase(AppFixture.DatabaseName);

    private static Guid nodeIdOf(IHost host) =>
        host.Services.GetRequiredService<IWolverineRuntime>().Options.UniqueNodeId;

    private static string uniqueContainerName() => $"wolverine_{Guid.NewGuid():N}";

    private Task<IHost> buildHostAsync(string containerName, DurabilityMode mode, string serviceName = "CustomContainer")
    {
        return Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.ServiceName = serviceName;
                opts.Durability.Mode = mode;

                opts.Services.AddSingleton(_fixture.Client);
                opts.UseCosmosDbPersistence(AppFixture.DatabaseName, cosmos => cosmos.UseContainer(containerName));

                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType<PartitionedSaga>();
            }).StartAsync();
    }

    private async Task<Guid[]> waitForNodesAsync(string containerName)
    {
        var container = database().GetContainer(containerName);
        var query = new QueryDefinition("SELECT VALUE c.nodeId FROM c WHERE c.docType = @docType")
            .WithParameter("@docType", DocumentTypes.Node);

        for (var attempt = 0; attempt < 60; attempt++)
        {
            var nodes = new List<Guid>();
            using var iterator = container.GetItemQueryIterator<Guid>(query);
            while (iterator.HasMoreResults)
            {
                nodes.AddRange(await iterator.ReadNextAsync());
            }

            if (nodes.Count > 0)
            {
                return nodes.ToArray();
            }

            await Task.Delay(500);
        }

        throw new TimeoutException($"No node registered itself in container {containerName}");
    }

    private static async Task<bool> existsAsync(Container container, string sagaId)
    {
        try
        {
            await container.ReadItemAsync<PartitionedSaga>(sagaId, PartitionKey.None);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    private async Task dropAsync(string containerName)
    {
        try
        {
            await database().GetContainer(containerName).DeleteContainerAsync();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
        }
    }
}
