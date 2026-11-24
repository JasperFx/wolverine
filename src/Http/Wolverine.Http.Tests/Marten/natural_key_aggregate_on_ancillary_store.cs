using System.Collections.Concurrent;
using Alba;
using IntegrationTests;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Shouldly;
using Wolverine.Http.Tests.AncillaryStore;
using Wolverine.Marten;

namespace Wolverine.Http.Tests.Marten;

/// <summary>
///     GH-4439 over HTTP: an endpoint routed to an ancillary store with <c>[MartenStore]</c>, taking a
///     natural-key-identified aggregate that is registered only on that store.
/// </summary>
/// <remarks>
///     <para>
///     Worth having separately from the message-handler suites because the two chain types resolve their
///     store at opposite points, and the HTTP one is the hostile case. A handler chain runs parameter
///     attributes before chain attributes but is rescued by the Phase-A eager policies, which pre-assign
///     <c>IChain.AncillaryStoreType</c> at <c>HandlerGraph.Compile</c>. An HTTP chain gets no such rescue:
///     <c>HttpChain.MapToRoute</c> triggers parameter matching from the <c>[WolverinePost]</c> attribute in
///     the constructor, long before <c>applyAttributesAndConfigureMethods</c> applies <c>[MartenStore]</c>,
///     and the eager policies are <c>IHandlerPolicy</c> so they never see an endpoint at all. The property
///     is still null at that moment; only <c>chain.DetermineAncillaryStoreType()</c> answers correctly.
///     </para>
///     <para>
///     The endpoints live in <c>Wolverine.Http.Tests.AncillaryStore</c> and this host is the only one that
///     discovers them — see that project's csproj. An endpoint whose parameter matching needs a store the
///     host has not registered throws from <c>HttpGraph.DiscoverEndpoints</c> and takes the whole host with
///     it, so these cannot live in the sample app or in this test assembly.
///     </para>
/// </remarks>
public class natural_key_aggregate_on_ancillary_store : IAsyncLifetime
{
    private IAlbaHost theHost = null!;
    private readonly WarningCapture theWarnings = new();

    public async ValueTask InitializeAsync()
    {
        // Pinned to Production: CI sets ASPNETCORE_ENVIRONMENT=Development, and in Development ASP.NET Core
        // adds the developer exception page, which turns an unhandled exception into a 500 instead of letting it
        // reach the test.
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            { EnvironmentName = Environments.Production });
        builder.Logging.AddProvider(theWarnings);

        builder.Host.UseWolverine(opts =>
        {
            // The only host in the suite that opts into these endpoints.
            opts.Discovery.IncludeAssembly(typeof(NkThingEndpoints).Assembly);

            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Durability.MessageStorageSchemaName = "nk_http_wolverine";
            opts.Policies.AutoApplyTransactions();
        });

        // The main store knows NOTHING about the aggregate -- if natural-key detection asks this store, it
        // finds nothing and the workflow reports that it cannot determine an aggregate id.
        builder.Services.AddMarten(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "nk_http_main";
            opts.Events.DatabaseSchemaName = "nk_http_main";
            opts.DisableNpgsqlLogging = true;
        }).IntegrateWithWolverine().UseLightweightSessions();

        builder.Services.AddMartenStore<INkThingStore>(opts =>
        {
            opts.Connection(Servers.PostgresConnectionString);
            opts.DatabaseSchemaName = "nk_http_things";
            opts.Events.DatabaseSchemaName = "nk_http_things";
            opts.DisableNpgsqlLogging = true;
            opts.Projections.Snapshot<NkThing>(SnapshotLifecycle.Inline);
            // Every host scanning the endpoint assembly builds the NkGadget endpoints too
            opts.Projections.Snapshot<NkGadget>(SnapshotLifecycle.Inline);
            opts.Projections.Snapshot<NkBin>(SnapshotLifecycle.Inline);
            opts.Projections.Snapshot<NkPart>(SnapshotLifecycle.Inline);
            opts.Projections.Snapshot<NkGadgetView>(SnapshotLifecycle.Async);
        }).IntegrateWithWolverine();

        // The string-identity natural-key endpoints in the endpoint assembly need this store
        builder.Services.AddMartenStore<INkStringStore>(m =>
        {
            m.Connection(Servers.PostgresConnectionString);
            m.DatabaseSchemaName = "nk_http_strings";
            m.Events.DatabaseSchemaName = "nk_http_strings";
            m.Events.StreamIdentity = JasperFx.Events.StreamIdentity.AsString;
            m.DisableNpgsqlLogging = true;
            m.Projections.Snapshot<NkStringGadget>(SnapshotLifecycle.Inline);
        }).IntegrateWithWolverine();

        builder.Services.AddResourceSetupOnStartup();
        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app =>
        {
            // A second stream claiming an existing natural key is a 409. Every chain, because the default
            // filter (IsTransactional) is evaluated before AutoApplyTransactions marks a plain session endpoint.
            app.MapWolverineEndpoints(opts => opts.MapConcurrencyFailuresToConflict(_ => true));
        });
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        await theHost.DisposeAsync();
    }

    // The natural-key lookup table outlives the run and a natural key names exactly ONE stream, so a
    // hardcoded key would pass once and then fail every rerun with DuplicateNaturalKeyException.
    private static ThingCode uniqueCode() => new("NK-" + Guid.NewGuid().ToString("N"));

    private async Task<(Guid StreamId, ThingCode Code)> seedThing(string title)
    {
        var streamId = Guid.NewGuid();
        var code = uniqueCode();

        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        session.Events.StartStream<NkThing>(streamId, new NkThingCreated(code, title));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (streamId, code);
    }

    private async Task<NkThing?> load(Guid streamId)
    {
        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        return await session.LoadAsync<NkThing>(streamId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task write_aggregate_endpoint_resolves_the_natural_key_through_the_ancillary_store()
    {
        var (streamId, code) = await seedThing("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkThing(code, "Renamed")).ToUrl("/nkthings/rename");

            // 204 comes from [EmptyResponse]. The status alone proves little -- without that attribute the
            // endpoint answers 200 and appends nothing at all -- so the document assertion is the real test.
            s.StatusCodeShouldBe(204);
        });

        var thing = await load(streamId);
        thing.ShouldNotBeNull();
        thing.Title.ShouldBe("Renamed");
    }

    [Fact]
    public async Task write_model_endpoint_resolves_the_natural_key_through_the_ancillary_store()
    {
        var (streamId, code) = await seedThing("Archive me");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new ArchiveNkThing(code)).ToUrl("/nkthings/archive");
            s.StatusCodeShouldBe(204);
        });

        var thing = await load(streamId);
        thing.ShouldNotBeNull();
        thing.IsArchived.ShouldBeTrue();
    }

    private async Task<(Guid StreamId, GadgetCode Code)> seedGadget(string name)
    {
        var streamId = Guid.NewGuid();
        var code = new GadgetCode("G-" + Guid.NewGuid().ToString("N"));

        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        session.Events.StartStream<NkGadget>(streamId, new NkGadgetCreated(code, name));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (streamId, code);
    }

    private async Task<NkGadget?> loadGadget(Guid streamId)
    {
        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        return await session.LoadAsync<NkGadget>(streamId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task write_aggregate_by_a_natural_key_in_the_route()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Renamed")).ToUrl($"/nkgadgets/{code.Value}/rename");
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("Renamed");
    }

    [Fact]
    public async Task write_aggregate_by_a_natural_key_in_the_query_string()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("By query")).ToUrl("/nkgadgets-by-query/rename").QueryString("code", code.Value);
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By query");
    }

    [Fact]
    public async Task an_unknown_natural_key_in_the_route_is_a_404()
    {
        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Nobody")).ToUrl("/nkgadgets/G-NONE/rename");
            s.StatusCodeShouldBe(404);
        });

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkgadgets/G-NONE");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task read_aggregate_by_a_natural_key_in_the_route()
    {
        var (streamId, code) = await seedGadget("Readable");

        var gadget = await theHost.GetAsJson<NkGadget>($"/nkgadgets/{code.Value}");
        gadget!.Id.ShouldBe(streamId);
        gadget.Name.ShouldBe("Readable");
    }

    [Fact]
    public async Task a_duplicate_natural_key_is_a_409()
    {
        var (_, code) = await seedGadget("First");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new CreateNkGadget(code.Value, "Second")).ToUrl("/nkgadgets");
            s.StatusCodeShouldBe(409);
        });
    }

    private async Task<Guid> seed<T>(object @event) where T : class
    {
        var streamId = Guid.NewGuid();

        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        session.Events.StartStream<T>(streamId, @event);
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return streamId;
    }

    private async Task<T?> load<T>(Guid streamId) where T : class
    {
        var store = theHost.Services.GetRequiredService<INkThingStore>();
        await using var session = store.LightweightSession();
        return await session.LoadAsync<T>(streamId, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task write_and_read_by_a_key_wrapping_an_int_in_the_route()
    {
        var number = new BinNumber(Random.Shared.Next(1, int.MaxValue));
        var streamId = await seed<NkBin>(new NkBinCreated(number, "Original"));

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RelabelNkBin("Relabelled")).ToUrl($"/nkbins/{number.Value}/relabel");
            s.StatusCodeShouldBe(204);
        });

        (await load<NkBin>(streamId))!.Label.ShouldBe("Relabelled");

        var bin = await theHost.GetAsJson<NkBin>($"/nkbins/{number.Value}");
        bin!.Id.ShouldBe(streamId);

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkbins/not-a-number");
            s.StatusCodeShouldBe(404);
        });
    }

    // GH-5954: {code} is named like NkGadget's key member, so it's the natural key without the key type
    [Fact]
    public async Task without_the_key_type_a_route_value_named_like_the_key_is_the_key()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("By inferred key")).ToUrl($"/nkgadgets-untyped/{code.Value}/rename");
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By inferred key");
    }

    [Theory]
    [InlineData("/nkhttp/inferred/{0}/rename")]
    [InlineData("/nkhttp/inferred-by-type/{0}/rename")]
    public async Task a_route_value_named_like_the_key_is_inferred_as_the_key(string route)
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Inferred")).ToUrl(string.Format(route, code.Value));
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("Inferred");
    }

    [Fact]
    public async Task a_read_infers_the_key_from_the_route_with_nothing_on_the_attribute()
    {
        var (streamId, code) = await seedGadget("Readable");

        var gadget = await theHost.GetAsJson<NkGadget>($"/nkhttp/inferred/{code.Value}");
        gadget!.Id.ShouldBe(streamId);

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkhttp/inferred/G-NONE");
            s.StatusCodeShouldBe(404);
        });
    }

    // GH-5954: a query string value the endpoint doesn't declare, named like the key
    [Fact]
    public async Task a_read_infers_the_key_from_the_query_string_with_no_endpoint_parameter()
    {
        var (streamId, code) = await seedGadget("Queried");

        var gadget = await theHost.GetAsJson<NkGadget>($"/nkhttp/inferred-query?code={code.Value}");
        gadget!.Id.ShouldBe(streamId);

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkhttp/inferred-query?code=G-NONE");
            s.StatusCodeShouldBe(404);
        });

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkhttp/inferred-query");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_write_infers_the_key_from_the_query_string_with_no_endpoint_parameter()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("By query")).ToUrl("/nkhttp/inferred-query/rename").QueryString("code", code.Value);
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By query");

        // Missing from the request: the write's identity is missing
        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Nobody")).ToUrl("/nkhttp/inferred-query/rename");
            s.StatusCodeShouldBe(400);
        });
    }

    [Theory]
    [InlineData("/nkhttp/constrained/{0}/rename")]
    [InlineData("/nkhttp/declared/{0}/rename")]
    [InlineData("/nkhttp/unmatched/{0}/rename")]
    public async Task a_route_value_the_endpoint_types_or_that_matches_no_key_is_the_stream_id(string route)
    {
        var (streamId, _) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("By stream id")).ToUrl(string.Format(route, streamId));
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By stream id");
    }

    [Fact]
    public async Task read_by_a_class_key_with_its_own_try_parse_in_the_route()
    {
        var code = new PartCode("P-" + Guid.NewGuid().ToString("N"));
        var streamId = await seed<NkPart>(new NkPartAdded(code, "Bolt"));

        var part = await theHost.GetAsJson<NkPart>($"/nkparts/{code.Value}");
        part!.Id.ShouldBe(streamId);
        part.Description.ShouldBe("Bolt");

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkparts/P-NONE");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task write_by_a_class_key_in_the_request_body()
    {
        var code = new PartCode("P-" + Guid.NewGuid().ToString("N"));
        var streamId = await seed<NkPart>(new NkPartAdded(code, "Bolt"));

        await theHost.Scenario(s =>
        {
            s.Post.Json(new DescribeNkPart(code, "Hex bolt")).ToUrl("/nkparts/describe");
            s.StatusCodeShouldBe(204);
        });

        (await load<NkPart>(streamId))!.Description.ShouldBe("Hex bolt");
    }

    [Fact]
    public async Task write_by_a_nullable_key_in_the_request_body()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetByBody(code, "By body")).ToUrl("/nkgadgets-by-body/rename");
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By body");
    }

    [Fact]
    public async Task write_by_a_query_string_value_without_the_key_type()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Untyped")).ToUrl("/nkgadgets-by-query-untyped/rename").QueryString("code", code.Value);
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("Untyped");
    }

    [Fact]
    public async Task an_optional_read_by_a_query_string_value()
    {
        var (_, code) = await seedGadget("Optional");

        var found = await theHost.GetAsJson<GadgetLookup>($"/nkgadgets-by-optional-query?code={code.Value}");
        found.ShouldBe(new GadgetLookup(true, "Optional"));

        var unknown = await theHost.GetAsJson<GadgetLookup>("/nkgadgets-by-optional-query?code=G-NONE");
        unknown.ShouldBe(new GadgetLookup(false, null));

        var missing = await theHost.GetAsJson<GadgetLookup>("/nkgadgets-by-optional-query");
        missing.ShouldBe(new GadgetLookup(false, null));
    }

    [Fact]
    public async Task a_required_read_by_a_nullable_query_string_value()
    {
        var (streamId, code) = await seedGadget("Required");

        var gadget = await theHost.GetAsJson<NkGadget>($"/nkgadgets-by-nullable-query?code={code.Value}");
        gadget!.Id.ShouldBe(streamId);

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkgadgets-by-nullable-query");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_write_by_a_nullable_query_string_value()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("By nullable query")).ToUrl("/nkgadgets-by-nullable-query/rename").QueryString("code", code.Value);
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By nullable query");
    }

    // GH-5947 (U1): a write with no identity is the caller's mistake, so a 400 rather than the 500 the load's exception
    // used to become. The first two are a stream id and a stream key; a natural key is refused the same way, as a TKey?
    // and as a struct wrapping a null string, which is what a missing value binds to.
    [Theory]
    [InlineData("/nkgadgets-by-stream-id-query/rename")]
    [InlineData("/nkstring-gadgets-by-stream-key-query/rename")]
    [InlineData("/nkgadgets-by-query/rename")]
    [InlineData("/nkgadgets-by-nullable-query/rename")]
    public async Task a_write_with_the_query_string_value_missing_is_a_400(string url)
    {
        var result = await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Nobody")).ToUrl(url);
            s.StatusCodeShouldBe(400);
            s.ContentTypeShouldBe("application/problem+json");
        });

        var problems = await result.ReadAsJsonAsync<ProblemDetails>();
        problems!.Detail!.ShouldContain("is missing from the request");
    }

    [Fact]
    public async Task a_write_with_an_empty_stream_id_in_the_request_body_is_a_400()
    {
        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetById(Guid.Empty, "Nobody")).ToUrl("/nkhttp/by-stream-id-in-body");
            s.StatusCodeShouldBe(400);
        });
    }

    [Fact]
    public async Task a_write_with_a_null_natural_key_in_the_request_body_is_a_400()
    {
        var result = await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetByBody(null, "Nobody")).ToUrl("/nkgadgets-by-body/rename");
            s.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<ProblemDetails>();
        problems!.Detail.ShouldBe("The GadgetCode natural key of the NkGadget to write to is missing from the request");
    }

    [Fact]
    public async Task a_read_with_the_query_string_value_missing_is_still_a_404()
    {
        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkhttp/read-aggregate-by-query");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task the_decider_workflow_loads_by_a_natural_key_in_the_request_body()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetByCode(code, "By decider")).ToUrl("/nkhttp/decider");
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By decider");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetByCode(default, "Nobody")).ToUrl("/nkhttp/decider");
            s.StatusCodeShouldBe(400);
        });
    }

    // GH-5947 (U2): [WriteAggregate("number")] with no key type, on a gadget whose key member is Code, reads {number} as
    // the stream id, so an endpoint for a gadget answers 404 to its natural key. Legal, so a warning rather than a
    // refusal.
    [Fact]
    public void an_untyped_route_value_for_an_aggregate_with_natural_keys_is_warned_about()
    {
        theWarnings.Messages.ShouldContain(x =>
            x.Contains("reads the route value 'number' as the stream id") &&
            x.Contains(nameof(NkHttpReviewEndpoints.RenameByUnmatchedName)) &&
            x.Contains("[WriteAggregate<GadgetCode>(\"number\")]"));

        // GH-5954: a route value named like the key, typed by a constraint or by the endpoint, is never ambiguous
        theWarnings.Messages.ShouldNotContain(x => x.Contains("reads the route value 'code'"));

        // A route value named like a stream id is taken to be one
        theWarnings.Messages.ShouldNotContain(x => x.Contains("reads the route value 'id'"));
    }

    [Fact]
    public async Task a_stream_id_route_value_still_loads_by_stream_id()
    {
        var (streamId, _) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("By stream id")).ToUrl($"/nkhttp/by-stream-id/{streamId}/rename");
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By stream id");
    }

    // ---------- every attribute over HTTP (NkHttpAttributeEndpoints) ----------

    [Theory]
    [InlineData("/nkhttp/write-model/{0}")]
    [InlineData("/nkhttp/aggregate/{0}")]
    [InlineData("/nkhttp/aggregate-generic/{0}")]
    [InlineData("/nkhttp/consistent/{0}")]
    [InlineData("/nkhttp/as-parameters/{0}/rename")]
    [InlineData("/nkhttp/exclusive/{0}/rename")]
    public async Task a_write_by_a_natural_key_in_the_route(string route)
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("By route")).ToUrl(string.Format(route, code.Value));
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By route");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Nobody")).ToUrl(string.Format(route, "G-NONE"));
            s.StatusCodeShouldBe(404);
        });
    }

    [Theory]
    [InlineData("/nkhttp/write-model-by-body")]
    [InlineData("/nkhttp/aggregate-by-body")]
    public async Task a_write_by_a_natural_key_in_the_request_body(string url)
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetByCode(code, "By body")).ToUrl(url);
            s.StatusCodeShouldBe(204);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("By body");
    }

    [Theory]
    [InlineData("/nkhttp/read-model/{0}")]
    [InlineData("/nkhttp/read-model-by-query?code={0}")]
    [InlineData("/nkhttp/read-aggregate-by-query?code={0}")]
    public async Task a_read_by_a_natural_key(string url)
    {
        var (streamId, code) = await seedGadget("Readable");

        var gadget = await theHost.GetAsJson<NkGadget>(string.Format(url, code.Value));
        gadget!.Id.ShouldBe(streamId);
        gadget.Name.ShouldBe("Readable");

        await theHost.Scenario(s =>
        {
            s.Get.Url(string.Format(url, "G-NONE"));
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task an_async_view_by_its_mapped_key()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadget("Viewed")).ToUrl($"/nkhttp/view/{code.Value}/rename");
            s.StatusCodeShouldBe(204);
        });

        // The view is Async with no daemon running: read at its current state through the gadget's lookup
        var view = await theHost.GetAsJson<NkGadgetView>($"/nkhttp/view/{code.Value}");
        view!.Id.ShouldBe(streamId);
        view.Name.ShouldBe("Viewed");

        await theHost.Scenario(s =>
        {
            s.Get.Url("/nkhttp/view/G-NONE");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_write_by_a_natural_key_checks_the_version()
    {
        var (streamId, code) = await seedGadget("Original");

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetAtVersion("At 1", 1)).ToUrl($"/nkhttp/versioned/{code.Value}/rename");
            s.StatusCodeShouldBe(204);
        });

        // Now at version 2, so 1 is stale: a concurrency failure, which this host maps to a 409
        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkGadgetAtVersion("Stale", 1)).ToUrl($"/nkhttp/versioned/{code.Value}/rename");
            s.StatusCodeShouldBe(409);
        });

        (await loadGadget(streamId))!.Name.ShouldBe("At 1");
    }

    [Fact]
    public async Task two_loads_by_natural_key_in_one_endpoint()
    {
        var (_, code) = await seedGadget("Paired");
        var number = new BinNumber(Random.Shared.Next(1, int.MaxValue));
        await seed<NkBin>(new NkBinCreated(number, "Shelf"));

        var both = await theHost.GetAsJson<GadgetAndBin>($"/nkhttp/gadget/{code.Value}/bin/{number.Value}");
        both.ShouldBe(new GadgetAndBin("Paired", number.Value));
    }

    private async Task<NkStringGadget?> loadStringGadget(string streamKey)
    {
        var store = theHost.Services.GetRequiredService<INkStringStore>();
        await using var session = store.LightweightSession();
        return await session.LoadAsync<NkStringGadget>(streamKey, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task write_and_read_by_a_natural_key_in_the_route_on_a_string_identity_store()
    {
        // Without the key type the route value would be the stream key, which any string parses into
        var streamKey = "stream-" + Guid.NewGuid().ToString("N");
        var code = new GadgetCode("G-" + Guid.NewGuid().ToString("N"));

        var store = theHost.Services.GetRequiredService<INkStringStore>();
        await using (var session = store.LightweightSession())
        {
            session.Events.StartStream<NkStringGadget>(streamKey, new NkStringGadgetCreated(code, "Original"));
            await session.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await theHost.Scenario(s =>
        {
            s.Post.Json(new RenameNkStringGadget("Renamed")).ToUrl($"/nkstring-gadgets/{code.Value}/rename");
            s.StatusCodeShouldBe(204);
        });

        (await loadStringGadget(streamKey))!.Name.ShouldBe("Renamed");

        var gadget = await theHost.GetAsJson<NkStringGadget>($"/nkstring-gadgets/{code.Value}");
        gadget!.Id.ShouldBe(streamKey);

        // The stream key is not the natural key
        await theHost.Scenario(s =>
        {
            s.Get.Url($"/nkstring-gadgets/{streamKey}");
            s.StatusCodeShouldBe(404);
        });
    }
}

/// <summary>Every warning the host logs, for the code generation warnings GH-5947 added.</summary>
internal class WarningCapture : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private class CapturingLogger(WarningCapture parent) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) parent.Messages.Enqueue(formatter(state, exception));
        }
    }
}
