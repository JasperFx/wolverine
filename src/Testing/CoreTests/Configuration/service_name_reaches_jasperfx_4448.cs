using JasperFx;
using JasperFx.Events.EventModeling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.EventModeling;
using Xunit;

namespace CoreTests.Configuration;

/// <summary>
///     GH-4448 — <c>WolverineOptions.ServiceName</c> and <c>JasperFxOptions.ServiceName</c> name the same
///     one running service, and the exchange between them runs both ways.
/// </summary>
/// <remarks>
///     <para>
///         ⚠️ <b>The whole thing only ever looked correct because the two defaults coincide.</b>
///         <c>JasperFxOptions.ServiceName</c> defaults to the entry assembly name, so a host whose
///         assembly is called what its service is called agrees with itself by accident. That is exactly
///         why this survived: it was reported from
///         <a href="https://github.com/JasperFx/fisher/issues/284">fisher#284</a>, where Stoat's assembly
///         is also called Stoat. Every test here names the service something the assembly is NOT.
///     </para>
///     <para>
///         The visible damage is <see cref="a_store_style_source_lands_on_the_same_canvas" />, and it is
///         the fact to read first if this ever goes red.
///     </para>
/// </remarks>
public class service_name_reaches_jasperfx_4448
{
    private static async Task<IHost> HostAsync(Action<WolverineOptions> configure,
        Action<JasperFxOptions>? critterStack = null)
        => await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                if (critterStack is not null)
                {
                    opts.Services.CritterStackDefaults(critterStack);
                }

                configure(opts);
            })
            .StartAsync(TestContext.Current.CancellationToken);

    /// <summary>
    ///     The direction that was missing: naming the Wolverine service is the documented way to name the
    ///     service, and nothing carried it back.
    /// </summary>
    [Fact]
    public async Task a_wolverine_service_name_reaches_jasperfx()
    {
        using var host = await HostAsync(opts => opts.ServiceName = "Ledgers");

        host.Services.GetRequiredService<JasperFxOptions>().ServiceName.ShouldBe("Ledgers");
        host.Services.GetRequiredService<WolverineOptions>().ServiceName.ShouldBe("Ledgers");
    }

    /// <summary>
    ///     The direction that already worked, kept because the fix is on the same two lines and a
    ///     write-back that ran unconditionally would break it.
    /// </summary>
    [Fact]
    public async Task a_jasperfx_service_name_still_reaches_wolverine()
    {
        using var host = await HostAsync(_ => { }, cr => cr.ServiceName = "Special");

        host.Services.GetRequiredService<WolverineOptions>().ServiceName.ShouldBe("Special");
        host.Services.GetRequiredService<JasperFxOptions>().ServiceName.ShouldBe("Special");
    }

    [Fact]
    public async Task neither_named_leaves_both_on_the_assembly_name()
    {
        using var host = await HostAsync(_ => { });

        host.Services.GetRequiredService<WolverineOptions>().ServiceName.ShouldBe("CoreTests");
        host.Services.GetRequiredService<JasperFxOptions>().ServiceName.ShouldBe("CoreTests");
    }

    /// <summary>
    ///     The ruling when a host says both, and it is the same precedence <c>ServiceName ??=</c> already
    ///     established: Wolverine's own value outranks the JasperFx fallback, so it outranks it in both
    ///     directions rather than in one.
    /// </summary>
    [Fact]
    public async Task wolverines_own_name_wins_when_both_are_named()
    {
        using var host = await HostAsync(opts => opts.ServiceName = "Ledgers", cr => cr.ServiceName = "Special");

        host.Services.GetRequiredService<WolverineOptions>().ServiceName.ShouldBe("Ledgers");
        host.Services.GetRequiredService<JasperFxOptions>().ServiceName.ShouldBe("Ledgers");
    }

    /// <summary>
    ///     ⚠️ <b>The symptom, and the reason this is worth fixing upstream rather than in one store.</b>
    /// </summary>
    /// <remarks>
    ///     A Critter Stack store's Event Model source falls back to <c>JasperFxOptions.ServiceName</c>
    ///     (fisher#280 and its Marten/Polecat twins) while <see cref="WolverineEventModelSource" /> names
    ///     its model from <c>WolverineOptions.ServiceName</c>. With the two disagreeing, one host
    ///     contributed <b>two</b> models and neither held both halves — a canvas split in two, reported as
    ///     <c>Expected exactly one assembled model, but got [Zebra, Stoat]</c>.
    ///     <para>
    ///         The stand-in below is a store's source in the one respect that matters — where it gets the
    ///         name — so this needs no store reference and holds for all three of them at once.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task a_store_style_source_lands_on_the_same_canvas()
    {
        using var host = await Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
                services.AddSingleton<IEventModelDefinitionSource, StoreStyleSource>())
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.ServiceName = "Ledgers";
            })
            .StartAsync(TestContext.Current.CancellationToken);

        var set = await WolverineEventModelExport.AssembleSetAsync(host.Services,
            token: TestContext.Current.CancellationToken);

        // One canvas, named what the host named itself — rather than one called Ledgers and one called
        // CoreTests, which is what the entry-assembly fallback produced.
        set.IsAmbiguous.ShouldBeFalse();
        set.Models.Select(x => x.Name).ShouldBe(["Ledgers"]);
    }

    /// <summary>
    ///     A Critter Stack store's Event Model source, reduced to the one line under test: the model is
    ///     named after <c>JasperFxOptions.ServiceName</c> unless the store was given a name of its own.
    /// </summary>
    private sealed class StoreStyleSource : IEventModelDefinitionSource
    {
        public Uri Subject { get; } = new("event-model://store-style");

        public Task<EventModelDescriptor?> TryCreateAsync(IServiceProvider services, CancellationToken token)
        {
            var name = services.GetService<JasperFxOptions>()?.ServiceName ?? "EventModel";
            return Task.FromResult<EventModelDescriptor?>(new EventModelDescriptor(name, []));
        }
    }
}
