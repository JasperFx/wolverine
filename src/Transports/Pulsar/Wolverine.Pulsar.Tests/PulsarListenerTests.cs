using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Xunit;

namespace Wolverine.Pulsar.Tests;

public class PulsarListenerTests
{
    [Fact]
    public async Task UnsubscribeOnClose()
    {
        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UsePulsar();
            opts.UnsubscribePulsarOnClose(PulsarUnsubscribeOnClose.Enabled);

            var topic = "persistent://public/default/test";
            opts.PublishMessage<PulsarListenerTestMessage>().ToPulsarTopic(topic);
            opts.ListenToPulsarTopic(topic).SubscriptionName("test");
        }).StartAsync();

        await host.MessageBus().PublishAsync(new PulsarListenerTestMessage());

        await host.StopAsync();

        var subscriptionExists = await SubscriptionExists();

        subscriptionExists.ShouldBeFalse();
    }

    [Fact]
    public async Task KeepSubscriptionOnClose()
    {
        var host = await Host.CreateDefaultBuilder().UseWolverine(opts =>
        {
            opts.UsePulsar();
            opts.UnsubscribePulsarOnClose(PulsarUnsubscribeOnClose.Disabled);

            var topic = "persistent://public/default/test";
            opts.PublishMessage<PulsarListenerTestMessage>().ToPulsarTopic(topic);
            opts.ListenToPulsarTopic(topic).SubscriptionName("test");
        }).StartAsync();

        await host.MessageBus()!.PublishAsync(new PulsarListenerTestMessage());

        await host.StopAsync();

        var subscriptionExists = await SubscriptionExists();

        subscriptionExists.ShouldBeTrue();
    }

    private async Task<bool> SubscriptionExists()
    {
        using var httpClient = new HttpClient();
        var response =
            await httpClient.GetAsync("http://localhost:8080/admin/v2/persistent/public/default/test/subscriptions");
        var subscriptions = await response.Content.ReadFromJsonAsync<JsonValue[]>();
        return subscriptions != null && subscriptions.Length != 0;
    }
}

public class PulsarListenerTestMessage;

public static class PulsarListenerTestMessageHandler
{
    public static void Handle(PulsarListenerTestMessage message)
    {
    }
}
