using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;

namespace Wolverine.AmazonSqs.Tests;

public class Bootstrapping
{
    [Fact]
    public async Task create_an_open_client()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => { opts.UseAmazonSqsTransportLocally(); }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSqsTransport();

        // Just a smoke test on configuration here
        var queueNames = await transport.Client!.ListQueuesAsync("wolverine", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task create_new_queue_on_startup()
    {
        var queueName = "wolverine-" + Guid.NewGuid();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoProvision();

                opts.ListenToSqsQueue("wolverine-" + queueName);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSqsTransport();

        // Just a smoke test on configuration here
        var queueNames = await transport.Client!.ListQueuesAsync("wolverine", TestContext.Current.CancellationToken);
        var queueUrl = queueNames.QueueUrls.FirstOrDefault(x => x.Contains(queueName));
        queueUrl.ShouldNotBeNull();

        await transport.Client!.DeleteQueueAsync(queueUrl, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task auto_purge_queue_on_startup_smoke_test()
    {
        var queueName = "wolverine-" + Guid.NewGuid();


        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoPurgeOnStartup().AutoProvision();

                opts.ListenToSqsQueue("wolverine-" + queueName);
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();
        var transport = options.AmazonSqsTransport();

        var queueNames = await transport.Client!.ListQueuesAsync("wolverine", TestContext.Current.CancellationToken);
        var queueUrl = queueNames.QueueUrls.FirstOrDefault(x => x.Contains(queueName));
        queueUrl.ShouldNotBeNull();

        await transport.Client!.DeleteQueueAsync(queueUrl, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task configure_listening()
    {
        var queueName = "wolverine-" + Guid.NewGuid();

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally().AutoPurgeOnStartup().AutoProvision();

                opts.ListenToSqsQueue(queueName, e =>
                {
                    e.VisibilityTimeout = 4;
                    e.MaxNumberOfMessages = 5;
                    e.WaitTimeSeconds = 6;
                });
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var options = host.Services.GetRequiredService<WolverineOptions>();

        var endpoint = options.Transports.GetOrCreateEndpoint(new Uri($"sqs://{queueName}"))
            .ShouldBeOfType<AmazonSqsQueue>();

        endpoint.VisibilityTimeout.ShouldBe(4);
        endpoint.MaxNumberOfMessages.ShouldBe(5);
        endpoint.WaitTimeSeconds.ShouldBe(6);
    }
}