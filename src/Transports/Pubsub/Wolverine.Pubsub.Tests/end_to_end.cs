// using JasperFx.Core;
// using Microsoft.Extensions.Hosting;
// using Shouldly;
// using Wolverine.Pubsub.Internal;
// using Wolverine.Configuration;
// using Wolverine.Tracking;
// using Xunit;

// namespace Wolverine.Pubsub.Tests;

// public class end_to_end : IAsyncLifetime {
//     private IHost? _host;

//     public async Task InitializeAsync() {
//         _host = await Host.CreateDefaultBuilder()
//             .UseWolverine(opts => {
//                 opts.UsePubsubTesting().AutoProvision();
//                 opts.ListenToPubsubTopic("send_and_receive");
//                 opts.PublishMessage<AsbMessage1>().ToPubsubTopic("send_and_receive");
//             }).StartAsync();
//     }

//     public async Task DisposeAsync() {
//         await (_host?.StopAsync() ?? Task.CompletedTask);
//     }

//     [Fact]
//     public void builds_response_and_retry_endpoints_by_default() {
//         var transport = _host?.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
//         var endpoints = transport?
//             .Endpoints()
//             .Where(x => x.Role == EndpointRole.System)
//             .OfType<PubsubTopic>().ToArray();

//         endpoints?.ShouldContain(x => x.TopicName.TopicId.StartsWith("wolverine.response."));
//         endpoints?.ShouldContain(x => x.TopicName.TopicId.StartsWith("wolverine.retries."));
//     }

//     [Fact]
//     public async Task disable_system_endpoints()
//     {
//         #region sample_disable_system_endpoints_in_google_cloud_pubsub

//         var host = await Host.CreateDefaultBuilder()
//             .UseWolverine(opts => {
//                 opts.UsePubsubTesting().AutoProvision().SystemEndpointsAreEnabled(false);
//                 opts.ListenToPubsubTopic("send_and_receive");
//                 opts.PublishAllMessages().ToPubsubTopic("send_and_receive");
//             }).StartAsync();

//         #endregion

//         var transport = host.GetRuntime().Options.Transports.GetOrCreate<PubsubTransport>();
//         var endpoints = transport
//             .Endpoints()
//             .Where(x => x.Role == EndpointRole.System)
//             .OfType<PubsubTopic>().ToArray();

//         endpoints.Any().ShouldBeFalse();

//     }

//     [Fact]
//     public async Task send_and_receive_a_single_message() {
//         var message = new AsbMessage1("Josh Allen");
//         var session = await _host?.TrackActivity()
//             .IncludeExternalTransports()
//             .Timeout(5.Minutes())
//             .SendMessageAndWaitAsync(message) ?? Task.CompletedTask;

//         session.Received.SingleMessage<AsbMessage1>()
//             .Name.ShouldBe(message.Name);
//     }

//     [Fact]
//     public async Task send_and_receive_multiple_messages_to_queue_with_session_identifier()
//     {
//         Func<IMessageContext, Task> sendMany = async c =>
//         {
//             await c.SendAsync(new AsbMessage2("One"), new DeliveryOptions { GroupId = "1" });
//             await c.SendAsync(new AsbMessage2("Two"), new DeliveryOptions { GroupId = "1" });
//             await c.SendAsync(new AsbMessage2("Three"), new DeliveryOptions { GroupId = "1" });
//         };

//         var session = await _host.TrackActivity()
//             .IncludeExternalTransports()
//             .Timeout(30.Seconds())
//             .ExecuteAndWaitAsync(sendMany);

//         var names = session.Received.MessagesOf<AsbMessage2>().Select(x => x.Name).ToArray();
//         names
//             .ShouldBe(["One", "Two", "Three"]);
//     }

//     [Fact]
//     public async Task send_and_receive_multiple_messages_to_subscription_with_session_identifier()
//     {
//         Func<IMessageContext, Task> sendMany = async bus =>
//         {
//             #region sample_sending_with_session_identifier

//             // bus is an IMessageBus
//             await bus.SendAsync(new AsbMessage3("Red"), new DeliveryOptions { GroupId = "2" });
//             await bus.SendAsync(new AsbMessage3("Green"), new DeliveryOptions { GroupId = "2" });
//             await bus.SendAsync(new AsbMessage3("Refactor"), new DeliveryOptions { GroupId = "2" });

//             #endregion
//         };

//         var session = await _host.TrackActivity()
//             .IncludeExternalTransports()
//             .Timeout(30.Seconds())
//             .ExecuteAndWaitAsync(sendMany);

//         session.Received.MessagesOf<AsbMessage3>().Select(x => x.Name)
//             .ShouldBe(new string[]{"Red", "Green", "Refactor"});
//     }
// }

// public record AsbMessage1(string Name);
// public record AsbMessage2(string Name);
// public record AsbMessage3(string Name);

// public static class AsbMessageHandler
// {
//     public static void Handle(AsbMessage1 message)
//     {
//         // nothing
//     }

//     public static void Handle(AsbMessage2 message)
//     {
//         // nothing
//     }

//     public static void Handle(AsbMessage3 message)
//     {
//         // nothing
//     }
// }
