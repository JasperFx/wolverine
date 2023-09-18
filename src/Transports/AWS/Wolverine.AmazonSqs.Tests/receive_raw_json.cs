using System.Net;
using Amazon.Runtime;
using Amazon.SQS;
using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using Shouldly;
using Wolverine.Tracking;

namespace Wolverine.AmazonSqs.Tests
{
    public class receive_raw_json : IAsyncLifetime
    {
        private IHost _host;

        public async Task InitializeAsync()
        {
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

                    opts.ListenToSqsQueue("receive_native_json");
                })
                .StartAsync();
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

        private static async Task SendRawJsonMessage(Guid id, TimeSpan timeout)
        {
            var credentials = new BasicAWSCredentials("ignore", "ignore");
            var cfg = new AmazonSQSConfig
            {
                ServiceURL = "http://localhost:4566",
            };

            // create local sqs client
            IAmazonSQS sqs = new AmazonSQSClient(credentials, cfg);

            string queueUrl = (await sqs.GetQueueUrlAsync("receive_native_json")).QueueUrl;

            var message = new MyNativeJsonMessage { Id = id };
            string messageBody = JsonConvert.SerializeObject(message);

            // send native message
            var sendMessageResponse = await sqs.SendMessageAsync(queueUrl, messageBody);

            ((int)sendMessageResponse.HttpStatusCode).ShouldBeGreaterThanOrEqualTo(200, customMessage: "Ensure Success StatusCode");
            ((int)sendMessageResponse.HttpStatusCode).ShouldBeLessThan(300, customMessage: "Ensure Success StatusCode");
        }

        public Task DisposeAsync()
        {
            return _host.StopAsync();
        }
    }

    public class MyNativeJsonMessage
    {
        public Guid Id { get; set; } = Guid.NewGuid();
    }

    public static class MyNativeJsonMessageHandler
    {
        public static void Handle(MyNativeJsonMessage message)
        {
            // nothing
        }
    }
}
