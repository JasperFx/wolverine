using IntegrationTests;

namespace Wolverine.AzureServiceBus.Tests;

public static class AzureServiceBusTesting
{
    private static bool _cleaned;

    /// <summary>
    /// Connect to the Azure Service Bus emulator from Wolverine's own docker-compose setup. This delegates
    /// to the shipping UseAzureServiceBusEmulator() API, but adds a one time cleanup of any objects left
    /// behind by previous test runs.
    /// </summary>
    public static AzureServiceBusConfiguration UseAzureServiceBusTesting(this WolverineOptions options)
    {
        if (!_cleaned)
        {
            _cleaned = true;
#pragma warning disable VSTHRD002 // Avoid problematic synchronous waits
            DeleteAllEmulatorObjectsAsync().GetAwaiter().GetResult();
#pragma warning restore VSTHRD002 // Avoid problematic synchronous waits
        }

        return options
            .UseAzureServiceBusEmulator(Servers.AzureServiceBusConnectionString,
                Servers.AzureServiceBusManagementConnectionString)
            .AutoProvision();
    }

    public static Task DeleteAllEmulatorObjectsAsync()
    {
        return DeleteAllEmulatorObjectsAsync(Servers.AzureServiceBusManagementConnectionString);
    }

    public static async Task DeleteAllEmulatorObjectsAsync(string connectionString)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await AzureServiceBusEmulatorExtensions.DeleteAllAzureServiceBusObjectsAsync(connectionString, cts.Token);
    }
}
