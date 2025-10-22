using JasperFx.Core;
using Shouldly;

namespace Wolverine.AzureServiceBus.Tests;

public static class AzureServiceBusTesting
{
    public static AzureServiceBusConfiguration UseAzureServiceBusTesting(this WolverineOptions options)
    {
        var connectionString = GetConnectionString();

        return options.UseAzureServiceBus(connectionString).AutoProvision();
    }

    public static string GetConnectionString()
    {
        var path = "../../../connection.txt".ToFullPath();
        File.Exists(path)
            .ShouldBeTrue(
                $"There needs to be a text file at '{path}' with the connection string to Azure Service Bus in order for these tests to be executed");

        var connectionString = File.ReadAllText(path).Trim();
        return connectionString;
    }
}