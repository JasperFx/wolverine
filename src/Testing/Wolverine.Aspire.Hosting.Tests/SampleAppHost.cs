using System.Net.Http.Json;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Shouldly;
using Xunit;

namespace Wolverine.Aspire.Hosting.Tests;

/// <summary>
/// Starts one of the AspireWith* sample AppHosts and drives its "pinger" project: every sample pinger publishes a
/// ping through the broker on POST /pings, handles it from the same broker, and reports how many it has handled on
/// GET /pings.
/// </summary>
public static class SampleAppHost
{
    public static readonly TimeSpan StartupTimeout = TimeSpan.FromMinutes(4);
    public static readonly TimeSpan DeliveryTimeout = TimeSpan.FromMinutes(1);

    public static async Task<DistributedApplication> StartAsync<TAppHost>(string brokerResource,
        CancellationToken cancellation) where TAppHost : class
    {
        var builder = await DistributedApplicationTestingBuilder.CreateAsync<TAppHost>(cancellation);
        var app = await builder.BuildAsync(cancellation);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(StartupTimeout);

        await app.StartAsync(timeout.Token);

        // The broker's own health check, which is what WaitFor() in the AppHost gates the service on
        await app.ResourceNotifications.WaitForResourceHealthyAsync(brokerResource, timeout.Token);

        // The pinger only reaches healthy if Wolverine started against the connection info the AppHost injected
        await app.ResourceNotifications.WaitForResourceHealthyAsync("pinger", timeout.Token);

        return app;
    }

    public static async Task ShouldRoundTripAPingAsync(this DistributedApplication app, CancellationToken cancellation)
    {
        using var client = app.CreateHttpClient("pinger");

        var sent = await client.PostAsync("/pings", null, cancellation);
        sent.EnsureSuccessStatusCode();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(DeliveryTimeout);

        var handled = 0;
        while (!timeout.IsCancellationRequested)
        {
            handled = await client.GetFromJsonAsync<int>("/pings", cancellation);
            if (handled > 0) return;

            await Task.Delay(250, cancellation);
        }

        handled.ShouldBeGreaterThan(0, "The pinger published a ping through the broker but never handled it");
    }

    /// <summary>
    /// The environment a project receives from the references <paramref name="reference"/> gives it, as the publish manifest
    /// spells it, so it can be asserted on without starting anything
    /// </summary>
    public static async Task<Dictionary<string, string>> EnvironmentOfAReferencingProjectAsync(
        this IDistributedApplicationBuilder builder, Func<IResourceBuilder<ContainerResource>, IResourceBuilder<ContainerResource>> reference)
    {
        var consumer = reference(builder.AddContainer("consumer", "consumer-image"));
        return await consumer.Resource.GetEnvironmentVariableValuesAsync(DistributedApplicationOperation.Publish);
    }
}
