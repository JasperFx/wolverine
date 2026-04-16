using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine.Configuration;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Configuration;

public class wire_tap_configuration : IAsyncLifetime
{
    private IHost _host = default!;
    private readonly RecordingWireTap _wireTap = new();

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<WireTapMessageHandler>();

                opts.Services.AddSingleton<IWireTap>(_wireTap);

                opts.Policies.AllLocalQueues(x => x.UseWireTap());
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task wire_tap_records_success_on_message_handled()
    {
        await _host.SendMessageAndWaitAsync(new WireTapMessage("hello"));

        // Give async fire-and-forget wire tap a moment
        await Task.Delay(500);

        _wireTap.Successes.ShouldContain(e => e.Message is WireTapMessage);
    }

    [Fact]
    public async Task wire_tap_not_called_without_configuration()
    {
        // Build a second host without wire tap configured
        var tap = new RecordingWireTap();
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<WireTapMessageHandler>();

                opts.Services.AddSingleton<IWireTap>(tap);
                // Notably: no UseWireTap() call
            }).StartAsync();

        await host.SendMessageAndWaitAsync(new WireTapMessage("no-tap"));
        await Task.Delay(500);

        tap.Successes.ShouldBeEmpty();
    }

    [Fact]
    public void wire_tap_not_set_on_endpoint_without_configuration()
    {
        var endpoint = new TestEndpoint(EndpointRole.Application);
        endpoint.WireTap.ShouldBeNull();
    }

    [Fact]
    public void use_wire_tap_sets_flag()
    {
        var endpoint = new TestEndpoint(EndpointRole.Application);
        endpoint.UseWireTap = true;
        endpoint.UseWireTap.ShouldBeTrue();
    }
}

public class keyed_wire_tap_configuration : IAsyncLifetime
{
    private IHost _host = default!;
    private readonly RecordingWireTap _defaultTap = new();
    private readonly RecordingWireTap _specialTap = new();

    public async Task InitializeAsync()
    {
        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType<WireTapMessageHandler>();

                opts.Services.AddSingleton<IWireTap>(_defaultTap);
                opts.Services.AddKeyedSingleton<IWireTap>("special", _specialTap);

                opts.PublishMessage<WireTapMessage>().ToLocalQueue("default-tapped");
                opts.LocalQueue("default-tapped").UseWireTap();

                opts.PublishMessage<KeyedWireTapMessage>().ToLocalQueue("special-tapped");
                opts.LocalQueue("special-tapped").UseWireTap("special");
            }).StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [Fact]
    public async Task default_wire_tap_is_used_without_key()
    {
        await _host.SendMessageAndWaitAsync(new WireTapMessage("default"));

        await Task.Delay(500);

        _defaultTap.Successes.ShouldContain(e => e.Message is WireTapMessage);
    }

    [Fact]
    public async Task keyed_wire_tap_is_used_with_service_key()
    {
        await _host.SendMessageAndWaitAsync(new KeyedWireTapMessage("special"));

        await Task.Delay(500);

        _specialTap.Successes.ShouldContain(e => e.Message is KeyedWireTapMessage);
        _defaultTap.Successes.ShouldNotContain(e => e.Message is KeyedWireTapMessage);
    }
}

public record WireTapMessage(string Text);

public record WireTapFailingMessage;

public record KeyedWireTapMessage(string Text);

public class WireTapMessageHandler
{
    public static void Handle(WireTapMessage message)
    {
        // No-op
    }

    public static void Handle(KeyedWireTapMessage message)
    {
        // No-op
    }

    public static void Handle(WireTapFailingMessage message)
    {
        throw new InvalidOperationException("Intentional failure for wire tap testing");
    }
}

public class RecordingWireTap : IWireTap
{
    public List<Envelope> Successes { get; } = new();
    public List<(Envelope envelope, Exception exception)> Failures { get; } = new();

    public ValueTask RecordSuccessAsync(Envelope envelope)
    {
        Successes.Add(envelope);
        return ValueTask.CompletedTask;
    }

    public ValueTask RecordFailureAsync(Envelope envelope, Exception exception)
    {
        Failures.Add((envelope, exception));
        return ValueTask.CompletedTask;
    }
}
