using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;
using Xunit;

namespace CoreTests.Bugs;

/// <summary>
/// Regression for GitHub issue #2896: a handler that depends on an IEnumerable&lt;T&gt; whose
/// registrations mix DI lifetimes (one singleton + one scoped of the same interface) used to
/// receive a null for the singleton element. JasperFx's inline IEnumerable codegen injects the
/// singleton element via [FromKeyedServices]; Wolverine now calls AddJasperFxEnumerableSingletonSupport()
/// at bootstrap so the keyed "mirror" registration exists and the singleton element is non-null.
/// </summary>
public class Bug_2896_mixed_lifetime_enumerable_dependency
{
    [Fact]
    public async Task mixed_lifetime_enumerable_dependency_resolves_every_element()
    {
        Bug2896Handler.Received = null;

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton<IBug2896Thing, Bug2896Singleton>();
                opts.Services.AddScoped<IBug2896Thing, Bug2896Scoped>();

                opts.Discovery.DisableConventionalDiscovery()
                    .IncludeType(typeof(Bug2896Handler));
            })
            .StartAsync();

        await host.MessageBus().InvokeAsync(new Bug2896Message());

        var received = Bug2896Handler.Received.ShouldNotBeNull();
        received.Length.ShouldBe(2);

        // The crux of #2896: the singleton element must not be null, and both lifetimes are present.
        received.OfType<Bug2896Singleton>().ShouldHaveSingleItem();
        received.OfType<Bug2896Scoped>().ShouldHaveSingleItem();

        // The singleton element shares the container's singleton instance.
        var containerSingleton = host.Services.GetServices<IBug2896Thing>().OfType<Bug2896Singleton>().Single();
        received.OfType<Bug2896Singleton>().Single().ShouldBeSameAs(containerSingleton);
    }
}

public record Bug2896Message;

public interface IBug2896Thing;

public class Bug2896Singleton : IBug2896Thing;

public class Bug2896Scoped : IBug2896Thing;

public static class Bug2896Handler
{
    public static IBug2896Thing[]? Received;

    public static void Handle(Bug2896Message message, IEnumerable<IBug2896Thing> things)
    {
        Received = things.ToArray();
    }
}
