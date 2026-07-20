using System.Reflection;
using MQTTnet;
using MQTTnet.Extensions.ManagedClient;
using NSubstitute;
using Shouldly;
using Wolverine.MQTT.Internals;

namespace Wolverine.MQTT.Tests;

public class MqttTransportJwtRefreshTests
{
    private readonly MqttTransport _transport;
    private readonly IManagedMqttClient _client;
    private readonly IMqttClient _internalClient;
    private readonly MqttJwtAuthenticationOptions _jwtOptions;

    public MqttTransportJwtRefreshTests()
    {
        _transport = new MqttTransport();

        // Mock the MQTT client so we don't need a real broker
        _client = Substitute.For<IManagedMqttClient>();
        _internalClient = Substitute.For<IMqttClient>();
        _client.InternalClient.Returns(_internalClient);
        _client.IsConnected.Returns(true);
        _transport.Client = _client;

        _jwtOptions = new MqttJwtAuthenticationOptions(
            GetTokenCallBack: () => Task.FromResult(Array.Empty<byte>()),
            RefreshPeriod: TimeSpan.FromHours(1)); // long enough that it never fires in tests
    }

    [Fact]
    public async Task on_client_connected_without_jwt_options_does_nothing()
    {
        // JwtAuthenticationOptions is null by default — no refresh needed
        var transport = new MqttTransport();

        await InvokeOnClientConnected(transport, SuccessConnectResult());

        GetRefreshCts(transport).ShouldBeNull();
    }

    [Fact]
    public async Task on_client_connected_with_jwt_options_creates_cancellation_token_source()
    {
        _transport.JwtAuthenticationOptions = _jwtOptions;

        await InvokeOnClientConnected(_transport, SuccessConnectResult());

        var cts = GetRefreshCts(_transport);
        cts.ShouldNotBeNull();
        cts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task on_client_connected_with_failure_result_code_does_nothing()
    {
        _transport.JwtAuthenticationOptions = _jwtOptions;

        // NotAuthorized result code — skip JWT refresh
        var args = ConnectResult(MqttClientConnectResultCode.NotAuthorized);
        await InvokeOnClientConnected(_transport, args);

        GetRefreshCts(_transport).ShouldBeNull();
    }

    [Fact]
    public async Task on_client_connected_reconnect_guard_cancels_old_cts()
    {
        _transport.JwtAuthenticationOptions = _jwtOptions;

        // First connect
        await InvokeOnClientConnected(_transport, SuccessConnectResult());
        var firstCts = GetRefreshCts(_transport);
        firstCts.ShouldNotBeNull();
        firstCts.IsCancellationRequested.ShouldBeFalse();

        // Second connect without a disconnect in between
        await InvokeOnClientConnected(_transport, SuccessConnectResult());
        var secondCts = GetRefreshCts(_transport);

        firstCts.IsCancellationRequested.ShouldBeTrue(
            "Old CTS should be cancelled on reconnect");
        secondCts.ShouldNotBeNull();
        secondCts.ShouldNotBeSameAs(firstCts,
            "A new CTS should be created on reconnect");
        secondCts.IsCancellationRequested.ShouldBeFalse();
    }

    [Fact]
    public async Task on_client_disconnected_cancels_and_nulls_refresh_cts()
    {
        _transport.JwtAuthenticationOptions = _jwtOptions;

        // Connect first
        await InvokeOnClientConnected(_transport, SuccessConnectResult());
        var cts = GetRefreshCts(_transport);
        cts.ShouldNotBeNull();

        // Disconnect
        await InvokeOnClientDisconnected(_transport);

        cts.IsCancellationRequested.ShouldBeTrue();
        GetRefreshCts(_transport).ShouldBeNull();
    }

    [Fact]
    public async Task on_client_disconnected_without_prior_connect_does_not_throw()
    {
        await InvokeOnClientDisconnected(_transport);

        GetRefreshCts(_transport).ShouldBeNull();
    }

    [Fact]
    public async Task dispose_async_cancels_refresh_cts()
    {
        _transport.JwtAuthenticationOptions = _jwtOptions;

        await InvokeOnClientConnected(_transport, SuccessConnectResult());
        var cts = GetRefreshCts(_transport);
        cts.ShouldNotBeNull();

        await _transport.DisposeAsync();

        cts.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task dispose_async_without_prior_connect_does_not_throw()
    {
        await _transport.DisposeAsync();

        GetRefreshCts(_transport).ShouldBeNull();
    }

    private static MqttClientConnectedEventArgs SuccessConnectResult()
    {
        return ConnectResult(MqttClientConnectResultCode.Success);
    }

    private static MqttClientConnectedEventArgs ConnectResult(MqttClientConnectResultCode code)
    {
        var result = new MqttClientConnectResult();
        // The ResultCode setter is internal in some MQTTnet TFMs;
        // use the backing field to stay TFM-agnostic
        var field = typeof(MqttClientConnectResult).GetField("<ResultCode>k__BackingField",
            BindingFlags.NonPublic | BindingFlags.Instance);
        field!.SetValue(result, code);
        return new MqttClientConnectedEventArgs(result);
    }

    private static CancellationTokenSource? GetRefreshCts(MqttTransport transport)
    {
        var field = typeof(MqttTransport).GetField("_refreshCts",
            BindingFlags.NonPublic | BindingFlags.Instance);
        return field?.GetValue(transport) as CancellationTokenSource;
    }

    private static async Task InvokeOnClientConnected(MqttTransport transport,
        MqttClientConnectedEventArgs args)
    {
        var method = typeof(MqttTransport).GetMethod("onClientConnected",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var task = (Task)method!.Invoke(transport, [args])!;
        await task;
    }

    private static async Task InvokeOnClientDisconnected(MqttTransport transport)
    {
        var method = typeof(MqttTransport).GetMethod("onClientDisconnected",
            BindingFlags.NonPublic | BindingFlags.Instance);

        var disconnectArgs = new MqttClientDisconnectedEventArgs(
            clientWasConnected: true,
            connectResult: null!,
            reason: MqttClientDisconnectReason.NormalDisconnection,
            reasonString: null,
            userProperties: null,
            exception: null);

        var task = (Task)method!.Invoke(transport, [disconnectArgs])!;
        await task;
    }
}
