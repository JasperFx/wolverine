using Aspire.Hosting;
using Aspire.Hosting.Testing;
using Xunit;

namespace Wolverine.Aspire.Hosting.Tests;

/// <summary>
/// GH-5624: the AspireWithKafka and AspireWithRabbitMq AppHosts start their services. Each service declared a single
/// TargetFramework beside the repository's net10.0;net11.0 TargetFrameworks list, so `dotnet run`, which is how an
/// AppHost starts a project, refused it with "Your project targets multiple frameworks".
/// </summary>
public class older_sample_apphosts
{
    [Fact]
    public async Task the_kafka_sample_starts_both_services()
    {
        await startAndWaitForHealthy<Projects.AspireWithKafka_AppHost>("kafka", "consumer", "publisher");
    }

    [Fact]
    public async Task the_rabbitmq_sample_starts_both_services()
    {
        await startAndWaitForHealthy<Projects.AspireWithRabbitMq_AppHost>("rabbitmq", "ponger", "pinger");
    }

    private static async Task startAndWaitForHealthy<TAppHost>(params string[] resources) where TAppHost : class
    {
        var cancellation = TestContext.Current.CancellationToken;

        var builder = await DistributedApplicationTestingBuilder.CreateAsync<TAppHost>(cancellation);
        await using var app = await builder.BuildAsync(cancellation);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(SampleAppHost.StartupTimeout);

        await app.StartAsync(timeout.Token);

        // Each service reaches healthy only once `dotnet run` started it and its /health answers
        foreach (var resource in resources)
        {
            await app.ResourceNotifications.WaitForResourceHealthyAsync(resource, timeout.Token);
        }
    }
}
