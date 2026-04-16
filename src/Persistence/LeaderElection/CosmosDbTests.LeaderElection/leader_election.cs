using System.Net;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;
using Wolverine.CosmosDb;
using Wolverine.CosmosDb.Internals;
using Wolverine.ComplianceTests;
using Xunit;
using Xunit.Abstractions;

namespace CosmosDbTests.LeaderElection;

public class leader_election : LeadershipElectionCompliance
{
    public static string ConnectionString => CosmosDbContainerFixture.ConnectionString;

    public const string DatabaseName = "wolverine_tests";

    public leader_election(ITestOutputHelper output) : base(output)
    {
    }

    protected override void configureNode(WolverineOptions opts)
    {
        opts.UseCosmosDbPersistence(DatabaseName);

        opts.Services.AddSingleton(new CosmosClient(ConnectionString, new CosmosClientOptions
        {
            HttpClientFactory = () =>
            {
                HttpMessageHandler httpMessageHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                return new HttpClient(httpMessageHandler);
            },
            ConnectionMode = ConnectionMode.Gateway,
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        }));
    }

    protected override async Task beforeBuildingHost()
    {
        var clientOptions = new CosmosClientOptions
        {
            HttpClientFactory = () =>
            {
                HttpMessageHandler httpMessageHandler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback =
                        HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                };
                return new HttpClient(httpMessageHandler);
            },
            ConnectionMode = ConnectionMode.Gateway,
            SerializerOptions = new CosmosSerializationOptions
            {
                PropertyNamingPolicy = CosmosPropertyNamingPolicy.CamelCase
            }
        };

        using var client = new CosmosClient(ConnectionString, clientOptions);

        // Ensure database and container exist
        var databaseResponse = await client.CreateDatabaseIfNotExistsAsync(DatabaseName);
        var containerProperties =
            new ContainerProperties(DocumentTypes.ContainerName, DocumentTypes.PartitionKeyPath);
        await databaseResponse.Database.CreateContainerIfNotExistsAsync(containerProperties);

        // Clear existing data
        var store = new CosmosDbMessageStore(client, DatabaseName,
            databaseResponse.Database.GetContainer(DocumentTypes.ContainerName), new WolverineOptions());
        await store.Admin.ClearAllAsync();
    }
}
