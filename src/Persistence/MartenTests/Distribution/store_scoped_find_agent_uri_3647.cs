using IntegrationTests;
using JasperFx.Events;
using JasperFx.Events.Projections;
using Marten;
using MartenTests.Distribution.TripDomain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;
using Shouldly;
using Weasel.Postgresql;
using Wolverine;
using Wolverine.Marten;
using Wolverine.Runtime.Agents;
using Wolverine.Tracking;
using Xunit;

namespace MartenTests.Distribution;

// GH-3647: FindAgentUriAsync had the same store-blindness GH-3618 fixed for the transient-rebuild path.
// It concatenates every store's agents and matches on the trailing shard path alone (MatchesShard compares
// AbsolutePath.EndsWith), so a projection name registered in more than one store resolved to whichever store
// was enumerated first. This URI is what tooling routes restart / pause / resume / rebuild through, so
// guessing wrong acts on another store's LIVE agent.
//
// Unlike store_scoped_transient_rebuild_3618, the projection is registered ASYNC in both stores here --
// that is what makes both of them resolve an agent URI at all, which is the precondition for the ambiguity.
public class store_scoped_find_agent_uri_3647 : IAsyncLifetime
{
    private IHost _host = null!;
    private IDocumentStore _main = null!;
    private IGh3647AncillaryStore _ancillary = null!;

    public async ValueTask InitializeAsync()
    {
        await using (var conn = new NpgsqlConnection(Servers.PostgresConnectionString))
        {
            await conn.OpenAsync();
            await conn.DropSchemaAsync("gh3647_main");
            await conn.DropSchemaAsync("gh3647_anc");
            await conn.CloseAsync();
        }

        _host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.Services.AddMarten(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "gh3647_main";
                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Services.AddMartenStore<IGh3647AncillaryStore>(m =>
                    {
                        m.DisableNpgsqlLogging = true;
                        m.Connection(Servers.PostgresConnectionString);
                        m.DatabaseSchemaName = "gh3647_anc";
                        m.Projections.Add<TripProjection>(ProjectionLifecycle.Async);
                    })
                    .IntegrateWithWolverine(x => x.UseWolverineManagedEventSubscriptionDistribution = true);

                opts.Discovery.DisableConventionalDiscovery();
            }).StartAsync();

        _main = _host.Services.GetRequiredService<IDocumentStore>();
        _ancillary = _host.Services.GetRequiredService<IGh3647AncillaryStore>();
    }

    public async ValueTask DisposeAsync()
    {
        _host.GetRuntime().Agents.DisableHealthChecks();
        await _host.StopAsync();
        _host.Dispose();
    }

    private EventSubscriptionAgentFamily theFamily =>
        _host.Services.GetServices<IEventSubscriptionAgentFamily>()
            .OfType<EventSubscriptionAgentFamily>().Single();

    private static string identityOf(IDocumentStore store) => ((IEventStore)store).Identity.ToString();

    [Fact]
    public async Task both_stores_resolve_an_agent_uri_for_the_shared_projection_name()
    {
        // The precondition for the ambiguity: two stores, one projection name, both distributed as agents.
        var agents = await theFamily.SupportedAgentsAsync();

        agents.Count(x => x.AbsolutePath.TrimEnd('/').EndsWith("/Trip/All", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(2, "both stores should contribute an agent for Trip:All");
    }

    [Fact]
    public async Task scoping_to_the_main_store_resolves_that_store_s_agent_uri()
    {
        var uri = await theFamily.FindAgentUriAsync(identityOf(_main), "Trip:All", null, TestContext.Current.CancellationToken);

        uri.ShouldNotBeNull();
        EventSubscriptionAgentFamily.StoreIdentityOf(uri!)
            .ShouldBe(identityOf(_main).ToLowerInvariant());
    }

    [Fact]
    public async Task scoping_to_the_ancillary_store_resolves_that_store_s_agent_uri()
    {
        // The mirror image. Asserting both directions is what proves the overload narrows by store rather
        // than merely agreeing with the family's enumeration order.
        var uri = await theFamily.FindAgentUriAsync(identityOf(_ancillary), "Trip:All", null, TestContext.Current.CancellationToken);

        uri.ShouldNotBeNull();
        EventSubscriptionAgentFamily.StoreIdentityOf(uri!)
            .ShouldBe(identityOf(_ancillary).ToLowerInvariant());
    }

    [Fact]
    public async Task the_two_store_scoped_lookups_return_different_uris()
    {
        // The single assertion that would have caught the bug: same shard identity, two stores, two answers.
        var main = await theFamily.FindAgentUriAsync(identityOf(_main), "Trip:All", null, TestContext.Current.CancellationToken);
        var ancillary = await theFamily.FindAgentUriAsync(identityOf(_ancillary), "Trip:All", null, TestContext.Current.CancellationToken);

        main.ShouldNotBeNull();
        ancillary.ShouldNotBeNull();
        main.ShouldNotBe(ancillary!);
    }

    /// <summary>
    ///     Documents the gap being closed. The store-blind overload still resolves a URI and is unchanged, but
    ///     it can only ever return one of the two, and the caller has no say in which.
    /// </summary>
    [Fact]
    public async Task the_store_blind_overload_still_resolves_but_cannot_be_steered()
    {
        var blind = await theFamily.FindAgentUriAsync("Trip:All", null, TestContext.Current.CancellationToken);
        blind.ShouldNotBeNull();

        var main = await theFamily.FindAgentUriAsync(identityOf(_main), "Trip:All", null, TestContext.Current.CancellationToken);
        var ancillary = await theFamily.FindAgentUriAsync(identityOf(_ancillary), "Trip:All", null, TestContext.Current.CancellationToken);

        new[] { main, ancillary }.ShouldContain(blind);
    }

    [Fact]
    public async Task a_store_outside_this_family_resolves_nothing()
    {
        (await theFamily.FindAgentUriAsync("someone-else:Marten", "Trip:All", null, TestContext.Current.CancellationToken)).ShouldBeNull(
            "so a caller looping over families can try the next one");
    }

    [Fact]
    public async Task an_unregistered_shard_in_a_known_store_resolves_nothing()
    {
        (await theFamily.FindAgentUriAsync(identityOf(_main), "DoesNotExist:All", null, TestContext.Current.CancellationToken)).ShouldBeNull();
    }

    [Fact]
    public async Task the_store_identity_match_is_case_insensitive()
    {
        // System.Uri lowercases the authority while EventStoreIdentity preserves casing (GH-3168), so the
        // lookup has to normalize the same way the family keys its stores.
        var uri = await theFamily.FindAgentUriAsync(identityOf(_main).ToUpperInvariant(), "Trip:All", null, TestContext.Current.CancellationToken);

        uri.ShouldNotBeNull();
        EventSubscriptionAgentFamily.StoreIdentityOf(uri!).ShouldBe(identityOf(_main).ToLowerInvariant());
    }
}

public interface IGh3647AncillaryStore : IDocumentStore;
