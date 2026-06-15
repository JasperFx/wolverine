using System;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Wolverine.Redis.Internal;
using Wolverine.Runtime;
using Xunit;

namespace Wolverine.Redis.Tests;

// GH-3110: the Redis transport can be configured with caller-supplied ConfigurationOptions or a
// caller-managed IConnectionMultiplexer (not only a connection string), so apps can plug in
// StackExchange.Redis extensions such as Microsoft.Azure.StackExchangeRedis for Entra ID /
// Managed Identity token refresh. Modeled on the Azure Service Bus transport's multiple
// connection modes.
public class redis_connection_source_configuration
{
    [Fact]
    public void caller_managed_multiplexer_is_used_as_is_and_never_disposed()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var transport = new RedisTransport(mux);

        // The supplied multiplexer is the single shared connection.
        transport.GetConnection().ShouldBeSameAs(mux);
    }

    [Fact]
    public async Task disposing_the_transport_does_not_dispose_a_caller_managed_multiplexer()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var transport = new RedisTransport(mux);

        _ = transport.GetConnection();

        await transport.DisposeAsync();

        // The caller owns the multiplexer's lifetime — Wolverine must not close or dispose it.
        await mux.DidNotReceive().CloseAsync(Arg.Any<bool>());
        mux.DidNotReceive().Dispose();
    }

    [Fact]
    public void connection_summary_reports_a_caller_managed_multiplexer_without_touching_it()
    {
        var mux = Substitute.For<IConnectionMultiplexer>();
        var transport = new RedisTransport(mux);

        transport.ConnectionSummary.ShouldBe("caller-managed IConnectionMultiplexer");
    }

    [Fact]
    public void connection_summary_masks_the_password_for_a_connection_string()
    {
        var transport = new RedisTransport("localhost:6379,password=sup3rs3cret");

        transport.ConnectionSummary.ShouldNotContain("sup3rs3cret");
        transport.ConnectionSummary.ShouldContain("****");
    }

    [Fact]
    public void connection_summary_masks_the_password_for_configuration_options()
    {
        var options = ConfigurationOptions.Parse("localhost:6379");
        options.Password = "sup3rs3cret";

        var transport = new RedisTransport(options);

        transport.ConnectionSummary.ShouldNotContain("sup3rs3cret");
        transport.ConnectionSummary.ShouldContain("****");
    }

    [Fact]
    public async Task configuration_options_overload_builds_a_working_connection()
    {
        var options = ConfigurationOptions.Parse(RedisContainerFixture.ConnectionString);
        var transport = new RedisTransport(options);

        // Wolverine builds and owns the multiplexer from these options.
        var pong = await transport.GetConnection().GetDatabase().PingAsync();
        pong.ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);

        await transport.DisposeAsync();
    }

    [Fact]
    public async Task bootstrapping_with_a_caller_managed_multiplexer_round_trips_and_preserves_it()
    {
        var streamKey = $"wolverine-tests-byo-mux-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The application creates and owns the multiplexer (here a plain one; in production this is
        // where Microsoft.Azure.StackExchangeRedis token refresh would be wired in).
        await using var mux = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);

        using (var host = await Host.CreateDefaultBuilder()
                   .UseWolverine(opts =>
                   {
                       opts.UseRedisTransport(mux).AutoProvision();

                       opts.ListenToRedisStream(streamKey, "g1")
                           .DefaultIncomingMessage<ByoMuxMessage>();

                       opts.PublishAllMessages().ToRedisStream(streamKey);
                       opts.Services.AddSingleton(tcs);
                   })
                   .StartAsync())
        {
            var bus = host.MessageBus();
            await bus.EndpointFor(new Uri($"redis://stream/0/{streamKey}"))
                .SendAsync(new ByoMuxMessage("123"));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.ShouldBe(tcs.Task);
        }

        // After the host that used it is disposed, the caller-managed multiplexer is still alive.
        mux.IsConnected.ShouldBeTrue();
        (await mux.GetDatabase().PingAsync()).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public void connection_summary_reports_a_caller_managed_multiplexer_factory()
    {
        var transport = new RedisTransport(_ => Substitute.For<IConnectionMultiplexer>());

        transport.ConnectionSummary.ShouldBe("caller-managed IConnectionMultiplexer factory");
    }

    [Fact]
    public async Task connection_factory_overload_round_trips_and_is_not_disposed_by_wolverine()
    {
        var streamKey = $"wolverine-tests-factory-mux-{Guid.NewGuid():N}";
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // The multiplexer is owned by the application here (not registered for disposal in the
        // container), so if it is still alive after the host is gone, Wolverine did not dispose it.
        await using var mux = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);

        using (var host = await Host.CreateDefaultBuilder()
                   .UseWolverine(opts =>
                   {
                       opts.UseRedisTransport(_ => mux).AutoProvision();

                       opts.ListenToRedisStream(streamKey, "g1")
                           .DefaultIncomingMessage<ByoMuxMessage>();

                       opts.PublishAllMessages().ToRedisStream(streamKey);
                       opts.Services.AddSingleton(tcs);
                   })
                   .StartAsync())
        {
            // The transport resolved its connection from the factory.
            var transport = host.Services.GetRequiredService<IWolverineRuntime>()
                .Options.Transports.GetOrCreate<RedisTransport>();
            transport.GetConnection().ShouldBeSameAs(mux);

            var bus = host.MessageBus();
            await bus.EndpointFor(new Uri($"redis://stream/0/{streamKey}"))
                .SendAsync(new ByoMuxMessage("123"));

            var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            completed.ShouldBe(tcs.Task);
        }

        mux.IsConnected.ShouldBeTrue();
        (await mux.GetDatabase().PingAsync()).ShouldBeGreaterThanOrEqualTo(TimeSpan.Zero);
    }

    [Fact]
    public async Task connection_factory_resolves_the_multiplexer_from_the_ioc_container()
    {
        var streamKey = $"wolverine-tests-factory-di-{Guid.NewGuid():N}";

        await using var mux = await ConnectionMultiplexer.ConnectAsync(RedisContainerFixture.ConnectionString);

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IConnectionMultiplexer>(mux);

                // Share the application's container-registered multiplexer with the transport.
                opts.UseRedisTransport(sp => sp.GetRequiredService<IConnectionMultiplexer>())
                    .AutoProvision();

                opts.ListenToRedisStream(streamKey, "g1").DefaultIncomingMessage<ByoMuxMessage>();
            })
            .StartAsync();

        var transport = host.Services.GetRequiredService<IWolverineRuntime>()
            .Options.Transports.GetOrCreate<RedisTransport>();

        transport.GetConnection().ShouldBeSameAs(mux);
    }

    public record ByoMuxMessage(string Id);

    public class ByoMuxHandler
    {
        private readonly TaskCompletionSource<bool> _tcs;
        public ByoMuxHandler(TaskCompletionSource<bool> tcs) => _tcs = tcs;
        public void Handle(ByoMuxMessage m) => _tcs.TrySetResult(true);
    }
}
