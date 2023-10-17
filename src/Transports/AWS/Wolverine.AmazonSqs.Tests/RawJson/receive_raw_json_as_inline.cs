using Amazon.Runtime;
using Amazon.SQS;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests.RawJson;

public class receive_raw_json_as_inline : IAsyncLifetime
{
    private IHost _host;
    private IHost _sender;
    private string theQueueName;

    public async Task InitializeAsync()
    {
        theQueueName = "receive_native_json_inline";
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UseAmazonSqsTransportLocally()
                    .ConfigureListeners(listeners =>
                    {
                        listeners.ReceiveRawJsonMessage(typeof(MyNativeJsonMessage));
                    })
                    .AutoProvision()
                    .AutoPurgeOnStartup();

                opts.ListenToSqsQueue(theQueueName).ProcessInline();
            })
            .StartAsync();

        _sender = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts
                    .UseAmazonSqsTransportLocally();
                    
                opts.Policies.DisableConventionalLocalRouting();
                opts.PublishAllMessages().ToSqsQueue(theQueueName).SendRawJsonMessage().SendInline();
            }).StartAsync();
    }

    [Fact]
    public async Task receive_native_json_message()
    {
        Guid id = Guid.NewGuid();

        var session = await _host
            .TrackActivity(10.Seconds())
            .WaitForMessageToBeReceivedAt<MyNativeJsonMessage>(_host)
            .ExecuteAndWaitAsync(_ => SendRawJsonMessage(id, 10.Seconds()));

        session.Received.SingleMessage<MyNativeJsonMessage>().Id.ShouldBe(id);
        session.Executed.SingleMessage<MyNativeJsonMessage>().Id.ShouldBe(id);
    }

    [Fact]
    public async Task send_and_receive_raw_json()
    {
        Guid id = Guid.NewGuid();

        var session = await _sender
            .TrackActivity(10.Seconds())
            .AlsoTrack(_host)
            .WaitForMessageToBeReceivedAt<MyNativeJsonMessage>(_host)
            .PublishMessageAndWaitAsync(new MyNativeJsonMessage { Id = id });
            
        session.Received.SingleMessage<MyNativeJsonMessage>().Id.ShouldBe(id);
    }
        
    [Fact]
    public async Task send_native_json_message()
    {
        Guid id = Guid.NewGuid();

        var session = await _host
            .TrackActivity(10.Seconds())
            .WaitForMessageToBeReceivedAt<MyNativeJsonMessage>(_host)
            .ExecuteAndWaitAsync(_ => SendRawJsonMessage(id, 10.Seconds()));

        session.Received.SingleMessage<MyNativeJsonMessage>().Id.ShouldBe(id);
        session.Executed.SingleMessage<MyNativeJsonMessage>().Id.ShouldBe(id);
    }

    private async Task SendRawJsonMessage(Guid id, TimeSpan timeout)
    {
        var credentials = new BasicAWSCredentials("ignore", "ignore");
        var cfg = new AmazonSQSConfig
        {
            ServiceURL = "http://localhost:4566",
        };

        // create local sqs client
        IAmazonSQS sqs = new AmazonSQSClient(credentials, cfg);

        string queueUrl = (await sqs.GetQueueUrlAsync(theQueueName)).QueueUrl;

        var message = new MyNativeJsonMessage { Id = id };
        string messageBody = JsonConvert.SerializeObject(message);

        // send native message
        var sendMessageResponse = await sqs.SendMessageAsync(queueUrl, messageBody);

        ((int)sendMessageResponse.HttpStatusCode).ShouldBeGreaterThanOrEqualTo(200, customMessage: "Ensure Success StatusCode");
        ((int)sendMessageResponse.HttpStatusCode).ShouldBeLessThan(300, customMessage: "Ensure Success StatusCode");
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        await _sender.StopAsync();
    }
}