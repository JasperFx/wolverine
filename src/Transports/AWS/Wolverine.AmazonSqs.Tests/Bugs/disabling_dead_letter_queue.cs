using IntegrationTests;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.AmazonSqs.Internal;
using Wolverine.Postgresql;
using Wolverine.Runtime;

namespace Wolverine.AmazonSqs.Tests.Bugs;

public class disabling_dead_letter_queue
{
    [Fact]
    public async Task do_not_create_dead_letter_queue()
    {
        #region sample_disabling_all_sqs_dead_letter_queueing

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    // Disable all native SQS dead letter queueing
                    .DisableAllNativeDeadLetterQueues()
                    .AutoProvision();

                opts.ListenToSqsQueue("incoming");
            }).StartAsync();

        #endregion

        // This is fugly
        var transport = host.Services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>()
            .Options.Transports.GetOrCreate<AmazonSqsTransport>();

        transport.Queues.Contains(AmazonSqsTransport.DeadLetterQueueName)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task do_not_use_default_dlq_when_all_listener_dlqs_are_configured()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.UseAmazonSqsTransportLocally()
                    .AutoProvision();

                opts.PublishMessage<ProductCreated>()
                    .ToSqsQueue("product-created");

                opts.ListenToSqsQueue("product-shipped")
                    .ConfigureDeadLetterQueue("product-shipped-error");
            }).StartAsync();

        var transport = host.Services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>()
            .Options.Transports.GetOrCreate<AmazonSqsTransport>();

        transport.Queues.Contains(AmazonSqsTransport.DeadLetterQueueName)
            .ShouldBeFalse();
    }

    [Fact]
    public async Task no_seriously_do_not_create_dlq()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(options =>
            {
                options.PersistMessagesWithPostgresql(Servers.PostgresConnectionString);
                options.UseAmazonSqsTransportLocally();

                options.Durability.Mode = DurabilityMode.Solo;

                options.ListenToSqsQueue("product-created")
                    .ConfigureDeadLetterQueue("product-created-error");
            }).StartAsync();

        var transport = host.Services.GetRequiredService<IWolverineRuntime>().As<WolverineRuntime>()
            .Options.Transports.GetOrCreate<AmazonSqsTransport>();

        transport.Queues.Contains(AmazonSqsTransport.DeadLetterQueueName)
            .ShouldBeFalse();
    }
}

public record ProductCreated;
