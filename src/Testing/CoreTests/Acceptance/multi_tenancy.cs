using System.Diagnostics;
using CoreTests.Configuration;
using JasperFx.MultiTenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Persistence;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

public class multi_tenancy : IAsyncLifetime
{
    private TenantedMessageTracker theTracker = new TenantedMessageTracker();
    private IHost _host = null!;

    public async ValueTask InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(theTracker);

                opts.Durability.TenantIdStyle = TenantIdStyle.ForceLowerCase;
            })
            .StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public void maybe_corrects_tenant_id_on_set()
    {
        var context = _host.MessageBus();
        context.TenantId = "WRONG_CASE";
        
        context.TenantId.ShouldBe("wrong_case");
    }

    [Fact]
    public async Task invoke_with_tenant()
    {
        var id = Guid.NewGuid();

        var tracked = await _host.ExecuteAndWaitAsync(async c =>
        {
            await c.InvokeForTenantAsync("foo", new TenantedMessage1(id));
        });

        tracked.Executed.SingleEnvelope<TenantedMessage1>().TenantId.ShouldBe("foo");
        tracked.Executed.SingleEnvelope<TenantedMessage2>().TenantId.ShouldBe("foo");
        tracked.Executed.SingleEnvelope<TenantedMessage3>().TenantId.ShouldBe("foo");
    }

    /// <summary>
    ///     GH-4640. <c>InvokeForTenantAsync</c> puts the raw string on <c>DeliveryOptions</c> and the
    ///     executor copies it straight onto the envelope, so <c>Envelope.TenantId</c> kept the caller's
    ///     casing while <c>MessageContext.TenantId</c> was normalised. Polecat and Fisher build their
    ///     session from the envelope, so they wrote the un-normalised <c>tenant_id</c> -- merely wrong on
    ///     SQL Server's case-insensitive collation, and unreachable on SQLite's BINARY one.
    ///     <c>MessageContext.ReadEnvelope</c> now writes the normalised value back, and every execution
    ///     path goes through it.
    /// </summary>
    [Fact]
    public async Task invoke_for_tenant_normalises_the_envelope_tenant_id()
    {
        var id = Guid.NewGuid();

        var tracked = await _host.ExecuteAndWaitAsync(async c =>
        {
            await c.InvokeForTenantAsync("MiXeD", new TenantedMessage1(id));
        });

        // The first envelope is the one that carried the caller's spelling; the cascades take their
        // tenant from the context and were always normalised.
        tracked.Executed.SingleEnvelope<TenantedMessage1>().TenantId.ShouldBe("mixed");
        tracked.Executed.SingleEnvelope<TenantedMessage2>().TenantId.ShouldBe("mixed");
        tracked.Executed.SingleEnvelope<TenantedMessage3>().TenantId.ShouldBe("mixed");
    }

    [Fact]
    public async Task invoke_with_tenant_with_expected_result()
    {
        var id = Guid.NewGuid();
        TenantedResult result = null!;

        var tracked = await _host.ExecuteAndWaitAsync(async c =>
        {
            result = await c.InvokeForTenantAsync<TenantedResult>("bar", new TenantedMessage1(id));
        });

        tracked.Executed.SingleEnvelope<TenantedMessage1>().TenantId.ShouldBe("bar");
        tracked.Executed.SingleEnvelope<TenantedMessage2>().TenantId.ShouldBe("bar");
        tracked.Executed.SingleEnvelope<TenantedMessage3>().TenantId.ShouldBe("bar");

        result.TenantId.ShouldBe("bar");
    }
}

public class TenantedMessageTracker
{
    public readonly Dictionary<Guid, string> TrackedOne = new();
    public readonly Dictionary<Guid, string> TrackedTwo = new();
    public readonly Dictionary<Guid, string> TrackedThree = new();
}

public record TenantedMessage1(Guid Id);
public record TenantedMessage2(Guid Id);
public record TenantedMessage3(Guid Id);

public record TenantedResult(string TenantId);

public static class TenantedHandler
{
    public static (TenantedResult, TenantedMessage2) Handle(TenantedMessage1 message, Envelope envelope, TenantedMessageTracker tracker, TenantId tenantId)
    {
        tenantId.Value.ShouldBe(envelope.TenantId);
        
        tracker.TrackedOne[message.Id] = envelope.TenantId!;
        return (new TenantedResult(envelope.TenantId!), new TenantedMessage2(message.Id));
    }

    public static TenantedMessage3 Handle(TenantedMessage2 message, Envelope envelope, TenantedMessageTracker tracker, TenantId tenantId)
    {
        tenantId.Value.ShouldBe(envelope.TenantId);

        tracker.TrackedTwo[message.Id] = envelope.TenantId!;
        return new TenantedMessage3(message.Id);
    }

    public static void Handle(TenantedMessage3 message, Envelope envelope, TenantedMessageTracker tracker, TenantId tenantId)
    {
        tenantId.Value.ShouldBe(envelope.TenantId);

        tracker.TrackedThree[message.Id] = envelope.TenantId!;
    }

    public static void Handle(TenantedResult result) => Debug.WriteLine("Got a tracked result");
}

public record SomeCommand;

#region sample_injecting_tenant_id
public static class SomeCommandHandler
{
    // Wolverine is keying off the type, the parameter name
    // doesn't really matter
    public static void Handle(SomeCommand command, TenantId tenantId)
    {
        Debug.WriteLine($"I got a command {command} for tenant {tenantId.Value}");
    }
}

#endregion
