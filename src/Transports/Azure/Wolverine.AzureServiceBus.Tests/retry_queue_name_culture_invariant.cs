using System.Globalization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Wolverine.AzureServiceBus.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.AzureServiceBus.Tests;

/// <summary>
/// The retry queue name is derived from the service name, which can hold an upper case "I". Lower casing it
/// with the current culture turns it into the dotless "ı" (U+0131) under tr-TR, which the identifier
/// sanitization then replaces, so a Turkish host provisioned a differently named retry queue than any other host.
/// </summary>
public class retry_queue_name_culture_invariant
{
    [Fact]
    public async Task retry_queue_name_does_not_depend_on_the_current_culture()
    {
        var options = new WolverineOptions { ServiceName = "InvoiceService" };
        options.UseAzureServiceBus("Endpoint=sb://fake.servicebus.windows.net/;SharedAccessKeyName=x;SharedAccessKey=y");
        var transport = options.Transports.GetOrCreate<AzureServiceBusTransport>();

        var runtime = Substitute.For<IWolverineRuntime>();
        runtime.Options.Returns(options);
        runtime.DurabilitySettings.Returns(options.Durability);
        runtime.Logger.Returns(NullLogger.Instance);

        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            await transport.InitializeEndpointsAsync(runtime);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        transport.RetryQueue.ShouldNotBeNull().QueueName.ShouldBe("wolverine.retries.invoiceservice");
    }
}
