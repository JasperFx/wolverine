using System.Collections.Concurrent;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Sqlite;

namespace SqliteTests.Transport;

[Collection("sqlite")]
public class multi_tenancy_with_multiple_files : SqliteContext, IAsyncLifetime
{
    private readonly SqliteTenantMessageTracker _tracker = new();
    private IHost _host = null!;
    private SqliteTestDatabase _main = null!;
    private SqliteTestDatabase _red = null!;
    private SqliteTestDatabase _blue = null!;

    public async Task InitializeAsync()
    {
        _main = Servers.CreateDatabase("sqlite_multi_tenant_main");
        _red = Servers.CreateDatabase("sqlite_multi_tenant_red");
        _blue = Servers.CreateDatabase("sqlite_multi_tenant_blue");

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;
                opts.Durability.ScheduledJobPollingTime = 200.Milliseconds();
                opts.Durability.ScheduledJobFirstExecution = 0.Seconds();
                opts.Durability.TenantCheckPeriod = 250.Milliseconds();

                opts.PersistMessagesWithSqlite(_main.ConnectionString)
                    .RegisterStaticTenants(tenants =>
                    {
                        tenants.Register("red", _red.ConnectionString);
                        tenants.Register("blue", _blue.ConnectionString);
                    })
                    .EnableMessageTransport(x => x.AutoProvision());

                opts.ListenToSqliteQueue("incoming").UseDurableInbox();

                opts.Services.AddSingleton(_tracker);

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(SqliteTenantMessageHandler));
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();

        _main.Dispose();
        _red.Dispose();
        _blue.Dispose();
    }

    [Fact]
    public async Task sends_and_receives_messages_across_main_and_tenant_files()
    {
        var endpoint = _host.MessageBus().EndpointFor("sqlite://incoming".ToUri());

        var red = new TenantFileMessage(Guid.NewGuid());
        var blue = new TenantFileMessage(Guid.NewGuid());
        var main = new TenantFileMessage(Guid.NewGuid());

        await endpoint.SendAsync(red, new DeliveryOptions { TenantId = "red" });
        await endpoint.SendAsync(blue, new DeliveryOptions { TenantId = "blue" });
        await endpoint.SendAsync(main);

        var allReceived = await Poll(30.Seconds(), () =>
            _tracker.Received.Any(x => x.Id == red.Id) &&
            _tracker.Received.Any(x => x.Id == blue.Id) &&
            _tracker.Received.Any(x => x.Id == main.Id));

        allReceived.ShouldBeTrue("Expected all tenant and main messages to be handled");

        _tracker.Received.First(x => x.Id == red.Id).TenantId.ShouldBe("red");
        _tracker.Received.First(x => x.Id == blue.Id).TenantId.ShouldBe("blue");

        var mainTenant = _tracker.Received.First(x => x.Id == main.Id).TenantId;
        (mainTenant.IsEmpty() || mainTenant == "*DEFAULT*").ShouldBeTrue();

        File.Exists(_main.DatabaseFile).ShouldBeTrue();
        File.Exists(_red.DatabaseFile).ShouldBeTrue();
        File.Exists(_blue.DatabaseFile).ShouldBeTrue();
    }

    [Fact]
    public async Task scheduled_messages_are_processed_in_tenant_files()
    {
        var endpoint = _host.MessageBus().EndpointFor("sqlite://incoming".ToUri());

        var red = new TenantFileMessage(Guid.NewGuid());
        var blue = new TenantFileMessage(Guid.NewGuid());

        await endpoint.SendAsync(red, new DeliveryOptions { TenantId = "red", ScheduleDelay = 2.Seconds() });
        await endpoint.SendAsync(blue, new DeliveryOptions { TenantId = "blue", ScheduleDelay = 2.Seconds() });

        await Task.Delay(300.Milliseconds());

        _tracker.Received.Any(x => x.Id == red.Id).ShouldBeFalse();
        _tracker.Received.Any(x => x.Id == blue.Id).ShouldBeFalse();

        var allReceived = await Poll(30.Seconds(), () =>
            _tracker.Received.Any(x => x.Id == red.Id) && _tracker.Received.Any(x => x.Id == blue.Id));

        allReceived.ShouldBeTrue("Expected scheduled tenant messages to be handled");

        _tracker.Received.First(x => x.Id == red.Id).TenantId.ShouldBe("red");
        _tracker.Received.First(x => x.Id == blue.Id).TenantId.ShouldBe("blue");
    }

    private static async Task<bool> Poll(TimeSpan timeout, Func<bool> condition)
    {
        var cts = new CancellationTokenSource(timeout);
        while (!cts.IsCancellationRequested)
        {
            if (condition()) return true;
            await Task.Delay(250.Milliseconds());
        }

        return condition();
    }
}

public record TenantFileMessage(Guid Id);

public class SqliteTenantMessageTracker
{
    public ConcurrentBag<(Guid Id, string? TenantId)> Received { get; } = new();
}

public class SqliteTenantMessageHandler
{
    public static void Handle(TenantFileMessage message, Envelope envelope, SqliteTenantMessageTracker tracker)
    {
        tracker.Received.Add((message.Id, envelope.TenantId));
    }
}
