using JasperFx.CodeGeneration.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Runtime;

/// <summary>
/// GH-4159. Code generation ignored an open-generic registration whenever the closed type was itself
/// concrete — which <c>Lazy&lt;T&gt;</c> is — so the conventional
/// <c>TryAddScoped(typeof(Lazy&lt;&gt;), typeof(LazyResolver&lt;&gt;))</c> adapter was never consulted
/// and the generated handler got <c>new Lazy&lt;IGreeter&gt;()</c> instead. That compiles and can never
/// work: the parameterless constructor uses <c>Activator.CreateInstance&lt;T&gt;()</c>, so the first
/// <c>.Value</c> throws <see cref="MissingMemberException"/> for any <c>T</c> without a public
/// parameterless constructor — which is every DI-registered service.
///
/// It failed silently and late. The reporter's host started clean, listeners attached, health checks
/// passed, and twelve integration tests recorded zero message execution: no dead letters and no failed
/// envelopes, because nothing ever ran.
///
/// The fix is in the code generator (jasperfx#715) and reaches Wolverine through the JasperFx
/// reference; this pins the behaviour end to end from a real handler.
/// </summary>
public class lazy_dependencies_resolve_through_their_registration
{
    [Fact]
    public async Task a_lazy_dependency_is_built_by_its_registered_adapter()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                // LazyResolver<T> takes an IServiceProvider, so the chain lands on service location.
                opts.ServiceLocationPolicy = ServiceLocationPolicy.AllowedButWarn;

                opts.Services.AddScoped<IGreeter, Greeter>();
                opts.Services.TryAddScoped(typeof(Lazy<>), typeof(LazyResolver<>));
            }).StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        LazyProbeState.Reset();

        await host.InvokeMessageAndWaitAsync(new LazyProbeCommand());

        // Before the fix this threw MissingMemberException the moment the handler touched .Value.
        LazyProbeState.Greeting.ShouldBe("hello");
    }
}

public record LazyProbeCommand;

public interface IGreeter
{
    string Greet();
}

public class Greeter : IGreeter
{
    public string Greet() => "hello";
}

/// <summary>The conventional adapter, and the shape GH-4159 was reported against.</summary>
public class LazyResolver<T>(IServiceProvider services)
    : Lazy<T>(() => services.GetRequiredService<T>()) where T : notnull;

public static class LazyProbeState
{
    public static string? Greeting;

    public static void Reset()
    {
        Greeting = null;
    }
}

public static class LazyProbeCommandHandler
{
    public static void Handle(LazyProbeCommand command, Lazy<IGreeter> greeter)
    {
        LazyProbeState.Greeting = greeter.Value.Greet();
    }
}
