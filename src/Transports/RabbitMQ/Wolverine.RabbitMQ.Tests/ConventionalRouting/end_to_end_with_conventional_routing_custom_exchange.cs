using JasperFx.Core;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine.Tracking;
using Xunit;

namespace Wolverine.RabbitMQ.Tests.ConventionalRouting;

public class end_to_end_with_conventional_routing_custom_exchange : IAsyncLifetime, IDisposable
{
    private IHost _receiver = null!;
    private IHost _sender = null!;

    public async Task InitializeAsync()
    {
        _receiver = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().UseConventionalRouting(conventions =>
                {
                    conventions.ExchangeNameForSending(type => type.Name + "_headers");
                    conventions.IncludeTypes(x => x == typeof(HeadersMessage));
                    conventions.ConfigureListeners((x, c) =>
                    {
                        if (c.MessageType == typeof(HeadersMessage))
                        {
                            x.BindToExchange<HeadersMessage>(ExchangeType.Headers, arguments: new Dictionary<string, object>()
                            {
                                {EnvelopeConstants.TenantIdKey, EnvelopeConstants.TenantIdKey}
                            });
                        }
                    });
                })
                .AutoProvision().AutoPurgeOnStartup();
            opts.ServiceName = "Receiver";
        });
        
        _sender = await WolverineHost.ForAsync(opts =>
        {
            opts.UseRabbitMq().UseConventionalRouting(conventions =>
                {
                    conventions.ExchangeNameForSending(type => type.Name + "_headers");
                    conventions.IncludeTypes(x => x == typeof(HeadersMessage));
                    conventions.ConfigureSending((x, c) =>
                    {
                        if (c.MessageType == typeof(HeadersMessage))
                        {
                            x.ExchangeType(ExchangeType.Headers);
                        }
                    });
            })
                .AutoProvision().AutoPurgeOnStartup();
            opts.DisableConventionalDiscovery();
            opts.ServiceName = "Sender";
        });


    }

    Task IAsyncLifetime.DisposeAsync() => Task.CompletedTask;

    public void Dispose()
    {
        _sender?.Dispose();
        _receiver?.Dispose();
    }

    [Fact]
    public async Task send_from_one_node_to_another_all_with_conventional_routing()
    {
        var session = await _sender.TrackActivity()
            .AlsoTrack(_receiver)
            .IncludeExternalTransports()
            .WaitForMessageToBeReceivedAt<HeadersMessage>(_receiver)
            .Timeout(30.Seconds())
            .SendMessageAndWaitAsync(new HeadersMessage(), new DeliveryOptions() {TenantId = "tenant-id"});

        var received = session
            .AllRecordsInOrder()
            .Where(x => x.Envelope?.Message?.GetType() == typeof(HeadersMessage))
            .Single(x => x.MessageEventType == MessageEventType.Received);

        received
            .ServiceName.ShouldBe("Receiver");
    }
}