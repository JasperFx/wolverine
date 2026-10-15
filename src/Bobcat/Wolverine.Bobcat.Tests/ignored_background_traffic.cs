using IntegrationTests;
using JasperFx.Core;
using Marten;
using Microsoft.Extensions.Hosting;
using Wolverine.Marten;
using Wolverine.Persistence;

namespace Wolverine.Bobcat.Tests.IgnoredTraffic;

// A host that embeds a tool with messaging of its own -- an embedded CritterWatch console polls for the life of the
// host -- always has an envelope in flight, so an act's tracked session never goes quiet and every act times out.
// An ignore rule written once on opts.Tracking applies to every tracked session against the host, the acts' own
// included, with nothing to configure on the spec.

public record ShipParcel(Guid ParcelId);
public record ParcelShipped(Guid ParcelId);

public class Parcel
{
    public Guid Id { get; set; }
    public static Parcel Create(ParcelShipped e) => new();
}

public static class ShipParcelHandler
{
    public static StartStream Handle(ShipParcel command)
        => Storage.StartStream<Parcel>(command.ParcelId, new ParcelShipped(command.ParcelId));
}

// Stands in for the embedded tool's polling
public record PollTheConsole(int Number);

public static class PollTheConsoleHandler
{
    public static Task Handle(PollTheConsole message) => Task.Delay(50);
}

public sealed class HostWithBackgroundTraffic : IAsyncLifetime
{
    public static readonly Uri ConsoleQueue = new("local://console");

    private readonly CancellationTokenSource _cancellation = new();
    private Task? _polling;

    public IHost Host { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Host = await Microsoft.Extensions.Hosting.Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddMarten(m =>
                    {
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "bobcat_ignored_traffic";
                        m.DisableNpgsqlLogging = true;
                    })
                    .IntegrateWithWolverine();

                opts.Policies.AutoApplyTransactions();
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(ShipParcelHandler))
                    .IncludeType(typeof(PollTheConsoleHandler));

                opts.PublishMessage<PollTheConsole>().ToLocalQueue("console");

                // Every tracked session against this host, and so every WolverineFx.Bobcat act, ignores the
                // embedded tool's traffic. For an embedded CritterWatch console that is its assemblies:
                // opts.Tracking.IgnoreMessagesFromAssembly(typeof(SomeCritterWatchMessage).Assembly);
                opts.Tracking.IgnoreEndpoint(ConsoleQueue);
            })
            .StartAsync();

        await Host.CleanAllMartenDataAsync();

        _polling = Task.Run(async () =>
        {
            var number = 0;
            while (!_cancellation.IsCancellationRequested)
            {
                await Host.MessageBus().PublishAsync(new PollTheConsole(++number));
                try
                {
                    await Task.Delay(10, _cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _cancellation.CancelAsync();
#pragma warning disable VSTHRD003
        if (_polling is not null) await _polling;
#pragma warning restore VSTHRD003

        await Host.StopAsync();
        Host.Dispose();
    }
}

public class ignored_background_traffic(HostWithBackgroundTraffic app) : WolverineSpec(app.Host),
    IClassFixture<HostWithBackgroundTraffic>
{
    [Fact]
    public async Task an_act_quiesces_on_the_applications_own_messages()
    {
        var theParcel = Guid.NewGuid();

        var recording = await Recordings.RecordAsync(async () =>
        {
            await WhenReceived(new ShipParcel(theParcel));
            ThenEvents(typeof(ParcelShipped));
        });

        recording.GatheredFailures().ShouldBeNull();

        LastAct.Session.ShouldNotBeNull();
        LastAct.Session.Status.ShouldBe(Wolverine.Tracking.TrackingStatus.Completed);
        LastAct.Session.AllRecordsInOrder().ShouldNotContain(x => x.Envelope!.Message is PollTheConsole);
        LastAct.MessagesSent.ShouldNotContain(x => x is PollTheConsole);
    }
}
