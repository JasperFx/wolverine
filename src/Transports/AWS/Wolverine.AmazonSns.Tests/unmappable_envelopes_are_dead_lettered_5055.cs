using Amazon.SQS.Model;
using IntegrationTests;
using JasperFx.Core;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Shouldly;
using Wolverine.AmazonSqs;
using Wolverine.Postgresql;
using Wolverine.Runtime;
using Wolverine.Tracking;

namespace Wolverine.AmazonSns.Tests;

/// <summary>
///     GH-5055, against LocalStack. An envelope the topic's mapper cannot map to an SNS message can never be sent, so
///     the durable sending agent moves it to dead letter storage and deletes its outbox row. The other envelopes of
///     the same batch are delivered. It used to be reported as sent with the rest, with only a log line left
/// </summary>
public class unmappable_envelopes_are_dead_lettered_5055 : IAsyncLifetime
{
    private const string Schema = "gh5055_sns";
    private readonly string _name = $"gh5055-sns-{Guid.NewGuid():N}";
    private IHost _host = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery().IncludeType(typeof(Serverless5044SnsHandler));
                opts.Policies.DisableConventionalLocalRouting();
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, Schema);

                opts.UseAmazonSqsTransportLocally().AutoProvision();
                opts.UseAmazonSnsTransportLocally().AutoProvision();

                // A mapper that throws on the poison message, standing in for any mapping bug
                opts.PublishMessage<Serverless5044SnsPoisonMessage>().ToSnsTopic(_name).SubscribeSqsQueue(_name)
                    .InteropWith(new Serverless5044PoisonMapper()).UseDurableOutbox();

                opts.Services.AddResourceSetupOnStartup();
            }).StartAsync(Token);

        await _host.GetRuntime().Storage.Admin.ClearAllAsync();
    }

    public async ValueTask DisposeAsync()
    {
        var options = _host.GetRuntime().Options;
        foreach (var queue in options.AmazonSqsTransport().Queues.Where(x => x.QueueName.StartsWith("gh5055")))
        {
            try { await queue.TeardownAsync(NullLogger.Instance); } catch { }
        }

        foreach (var topic in options.AmazonSnsTransport().Topics.Where(x => x.TopicName.StartsWith("gh5055")))
        {
            try { await topic.TeardownAsync(NullLogger.Instance); } catch { }
        }

        await _host.StopAsync();
        _host.Dispose();
    }

    private async Task<long> countAsync(string table)
    {
        await using var conn = new NpgsqlConnection(Servers.PostgresConnectionString);
        await conn.OpenAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"select count(*) from {Schema}.{table}";
        return (long)(await cmd.ExecuteScalarAsync(Token))!;
    }

    private async Task<int> receiveAsync(int expected)
    {
        var sqs = _host.GetRuntime().Options.AmazonSnsTransport().SqsClient!;
        var url = (await sqs.GetQueueUrlAsync(_name, Token)).QueueUrl;
        var count = 0;
        var deadline = DateTimeOffset.UtcNow.Add(15.Seconds());
        while (DateTimeOffset.UtcNow < deadline && count < expected)
        {
            var response = await sqs.ReceiveMessageAsync(
                new ReceiveMessageRequest(url) { MaxNumberOfMessages = 10, WaitTimeSeconds = 1 }, Token);
            var messages = response.Messages ?? [];
            count += messages.Count;
            foreach (var message in messages) await sqs.DeleteMessageAsync(url, message.ReceiptHandle, Token);
        }

        return count;
    }

    [Fact]
    public async Task two_of_three_are_delivered_and_the_unmappable_one_is_dead_lettered()
    {
        await _host.MessageBus().InvokeAsync(
            new Serverless5044SnsPoisonTrigger([1, Serverless5044PoisonMapper.PoisonNumber, 2]), Token);

        (await receiveAsync(2)).ShouldBe(2);

        // The dead letter is written and the outbox rows are deleted in the background
        var deadline = DateTimeOffset.UtcNow.Add(10.Seconds());
        while (DateTimeOffset.UtcNow < deadline &&
               (await countAsync("wolverine_dead_letters") == 0 || await countAsync("wolverine_outgoing_envelopes") > 0))
        {
            await Task.Delay(100, Token);
        }

        (await countAsync("wolverine_dead_letters")).ShouldBe(1);
        (await countAsync("wolverine_outgoing_envelopes")).ShouldBe(0);
    }
}
