using JasperFx.Core;
using Shouldly;

namespace Wolverine.AzureServiceBus.Tests;

public static class TestingExtensions
{
    public static AzureServiceBusConfiguration UseAzureServiceBusTesting(this WolverineOptions options)
    {
        var path = "../../../connection.txt".ToFullPath();
        File.Exists(path)
            .ShouldBeTrue(
                $"There needs to be a text file at '{path}' with the connection string to Azure Service Bus in order for these tests to be executed");

        var connectionString = File.ReadAllText(path).Trim();

        return options.UseAzureServiceBus(connectionString).AutoProvision();
    }
}