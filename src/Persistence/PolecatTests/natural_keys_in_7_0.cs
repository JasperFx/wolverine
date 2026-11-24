using System.Diagnostics.CodeAnalysis;
using Alba;
using IntegrationTests;
using JasperFx;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.Http;
using Wolverine.Http.Polecat;
using Wolverine.Polecat;
using Wolverine.Runtime;
using Wolverine.Tracking;
using JasperFx.CodeGeneration;
using JasperFx.Resources;
using Polecat;

namespace PolecatTests;

/// <summary>
///     GH-5901, GH-5947 and GH-5948 on Polecat: reads by natural key, the generic <c>[WriteAggregate&lt;TKey&gt;]</c>,
///     <c>[ReadAggregate&lt;TKey&gt;]</c> and <c>[Aggregate&lt;TKey&gt;]</c>, the decider workflow by natural key, and
///     the HTTP status codes, against Polecat's primary store. The Fisher and Marten suites cover the same ground.
/// </summary>
public class natural_keys_in_7_0 : IAsyncLifetime
{
    private IAlbaHost theHost = null!;


    public async ValueTask InitializeAsync()
    {

        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(PcNkSeatHandlers))
                .IncludeType(typeof(PcNkSeatOtherHandlers))
                .IncludeType(typeof(PcNkSeatDecider));
            opts.Discovery.IncludeAssembly(typeof(natural_keys_in_7_0).Assembly);

            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        builder.Services.AddPolecat(m =>
            {
                m.ConnectionString = Servers.SqlServerConnectionString;
                m.DatabaseSchemaName = "nk_7_0";
                m.Projections.Snapshot<PcNkSeat>(SnapshotLifecycle.Inline);
            })
            .IntegrateWithWolverine()
            .UseLightweightSessions();

        builder.Services.AddResourceSetupOnStartup();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a natural key endpoint", type => type != typeof(PcNkSeatEndpoints)))));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();

    }

    private IDocumentStore theStore => theHost.Services.GetRequiredService<IDocumentStore>();

    // A natural key names exactly one stream, and the lookup table outlives the run
    private static PcNkSeatCode uniqueCode() => new("S-" + Guid.NewGuid().ToString("N"));

    private async Task<(Guid StreamId, PcNkSeatCode Code)> addSeat()
    {
        var streamId = Guid.NewGuid();
        var code = uniqueCode();

        await using var session = theStore.LightweightSession();
        session.Events.StartStream<PcNkSeat>(streamId, new PcNkSeatAdded(code));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (streamId, code);
    }

    private async Task<PcNkSeat> loadSeat(Guid streamId)
    {
        await using var session = theStore.LightweightSession();
        return (await session.LoadAsync<PcNkSeat>(streamId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
    }

    private string sourceFor<T>()
    {
        var handlers = theHost.GetRuntime().Handlers;
        handlers.HandlerFor<T>();
        return handlers.ChainFor<T>()!.SourceCode!;
    }

    [Fact]
    public async Task write_aggregate_names_the_key_type()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new HoldPcNkSeat(code, "Alice"));

        (await loadSeat(streamId)).Holder.ShouldBe("Alice");
    }

    [Fact]
    public async Task read_aggregate_by_the_named_key_type()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new ViewPcNkSeat(code));

        PcNkSeatHandlers.LastSeat.ShouldNotBeNull().Id.ShouldBe(streamId);
        sourceFor<ViewPcNkSeat>().ShouldContain(
            $"FetchLatest<{typeof(PcNkSeat).FullName}, {typeof(PcNkSeatCode).FullName}>");
    }

    [Fact]
    public async Task read_aggregate_by_an_inferred_key()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new PeekPcNkSeat(code));

        PcNkSeatHandlers.LastSeat.ShouldNotBeNull().Id.ShouldBe(streamId);
    }

    [Fact]
    public async Task an_optional_read_by_a_null_key_finds_nothing()
    {
        await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new MaybePcNkSeat(null));

        PcNkSeatHandlers.LastMaybe.ShouldBeNull();
    }

    [Fact]
    public async Task the_decider_workflow_loads_by_the_natural_key()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new HoldPcNkSeatByDecider(code, "Bob"));

        (await loadSeat(streamId)).Holder.ShouldBe("Bob");
    }

    [Fact]
    public async Task the_decider_workflow_by_key_at_a_version()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new HoldPcNkSeatAtVersion(code, "Current", 1));
        (await loadSeat(streamId)).Holder.ShouldBe("Current");

        // Now at version 2, so 1 is stale
        await Should.ThrowAsync<ConcurrencyException>(() =>
            theHost.InvokeMessageAndWaitAsync(new HoldPcNkSeatAtVersion(code, "Stale", 1)));
        (await loadSeat(streamId)).Holder.ShouldBe("Current");
    }

    [Fact]
    public void a_type_argument_and_a_different_natural_key_are_refused()
    {
        var ex = Should.Throw<Exception>(() =>
            theHost.GetRuntime().Handlers.HandlerFor<HoldPcNkSeatTwice>());
        ex.ToString().ShouldContain("names two different natural key types");
    }

    // ---------- over HTTP ----------

    [Fact]
    public async Task aggregate_by_the_named_key_type_in_the_route()
    {
        var (streamId, code) = await addSeat();

        await theHost.Scenario(s =>
        {
            s.Post.Json(new HoldPcNkSeatRequest("Carol")).ToUrl($"/pcnk/seats/{code.Value}/hold");
            s.StatusCodeShouldBe(204);
        });

        (await loadSeat(streamId)).Holder.ShouldBe("Carol");

        // Unlike Marten, Polecat's FetchForWriting<T, TKey> throws for a key no stream has, rather than handing back
        // an empty stream for the not-found check to answer 404 to
        var ex = await Should.ThrowAsync<InvalidOperationException>(() => theHost.Scenario(s =>
        {
            s.Post.Json(new HoldPcNkSeatRequest("Nobody")).ToUrl("/pcnk/seats/S-NONE/hold");
        }));
        ex.Message.ShouldContain("No active stream found for natural key");
    }

    [Fact]
    public async Task read_by_the_named_key_type_in_the_route()
    {
        var (streamId, code) = await addSeat();

        var seat = await theHost.GetAsJson<PcNkSeat>($"/pcnk/seats/{code.Value}");
        seat.ShouldNotBeNull().Id.ShouldBe(streamId);

        await theHost.Scenario(s =>
        {
            s.Get.Url("/pcnk/seats/S-NONE");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_write_with_no_key_in_the_request_body_is_a_400()
    {
        var result = await theHost.Scenario(s =>
        {
            s.Post.Json(new HoldPcNkSeat(null!, "Nobody")).ToUrl("/pcnk/seats/hold");
            s.StatusCodeShouldBe(400);
        });

        var problems = await result.ReadAsJsonAsync<ProblemDetails>();
        problems!.Detail!.ShouldContain("is missing from the request");
    }

    [Fact]
    public async Task the_decider_workflow_by_a_key_in_the_request_body()
    {
        var (streamId, code) = await addSeat();

        await theHost.Scenario(s =>
        {
            s.Post.Json(new HoldPcNkSeatByDecider(code, "Dave")).ToUrl("/pcnk/seats/decide");
            s.StatusCodeShouldBe(204);
        });

        (await loadSeat(streamId)).Holder.ShouldBe("Dave");
    }
}

// A class key binds from the route only through its own TryParse
public record PcNkSeatCode(string Value)
{
    public static bool TryParse(string? value, IFormatProvider? provider, [NotNullWhen(true)] out PcNkSeatCode? code)
    {
        code = string.IsNullOrWhiteSpace(value) ? null : new PcNkSeatCode(value);
        return code != null;
    }
}

public class PcNkSeat
{
    public Guid Id { get; set; }

    [NaturalKey]
    public PcNkSeatCode Code { get; set; } = null!;

    public string? Holder { get; set; }

    [NaturalKeySource]
    public void Apply(PcNkSeatAdded e) => Code = e.Code;

    public void Apply(PcNkSeatHeld e) => Holder = e.Holder;
}

public record PcNkSeatAdded(PcNkSeatCode Code);
public record PcNkSeatHeld(string Holder);

public record HoldPcNkSeat(PcNkSeatCode Code, string Holder);
public record HoldPcNkSeatTwice(PcNkSeatCode Code, string Holder);
public record ViewPcNkSeat(PcNkSeatCode Code);
public record PeekPcNkSeat(PcNkSeatCode Code);
public record MaybePcNkSeat(PcNkSeatCode? Code);
public record HoldPcNkSeatByDecider(PcNkSeatCode Code, string Holder);
public record HoldPcNkSeatAtVersion(PcNkSeatCode Code, string Holder, long Version);
public record HoldPcNkSeatRequest(string Holder);

#region sample_wolverine_polecat_natural_key_type_argument
public static class PcNkSeatHandlers
{
    public static PcNkSeat? LastSeat;
    public static PcNkSeat? LastMaybe;

    // Name the key type when the message carries more than one key, or to say so up front
    public static PcNkSeatHeld Handle(HoldPcNkSeat command, [WriteAggregate<PcNkSeatCode>] PcNkSeat seat)
        => new(command.Holder);

    // Reads by natural key
    public static void Handle(ViewPcNkSeat query, [ReadAggregate<PcNkSeatCode>] PcNkSeat seat) => LastSeat = seat;
}
#endregion

public static class PcNkSeatOtherHandlers
{
    public static void Handle(PeekPcNkSeat query, [ReadAggregate] PcNkSeat seat)
        => PcNkSeatHandlers.LastSeat = seat;

    public static void Handle(MaybePcNkSeat query, [ReadAggregate(Required = false)] PcNkSeat? seat)
        => PcNkSeatHandlers.LastMaybe = seat;

#pragma warning disable JFXEVT016, JFXEVT017
    public static PcNkSeatHeld Handle(HoldPcNkSeatTwice command,
        [WriteAggregate<PcNkSeatCode>(NaturalKey = typeof(string))] PcNkSeat seat) => new(command.Holder);
#pragma warning restore JFXEVT016, JFXEVT017
}

[AggregateHandler]
public static class PcNkSeatDecider
{
    public static PcNkSeatHeld Handle(HoldPcNkSeatByDecider command, PcNkSeat seat) => new(command.Holder);

    public static PcNkSeatHeld Handle(HoldPcNkSeatAtVersion command, PcNkSeat seat) => new(command.Holder);
}

public static class PcNkSeatEndpoints
{
    [WolverinePost("/pcnk/seats/{code}/hold")]
    [EmptyResponse]
    public static PcNkSeatHeld Hold(HoldPcNkSeatRequest request, [Aggregate<PcNkSeatCode>("code")] PcNkSeat seat)
        => new(request.Holder);

    [WolverineGet("/pcnk/seats/{code}")]
    public static PcNkSeat Get([ReadAggregate<PcNkSeatCode>("code")] PcNkSeat seat) => seat;

    [WolverinePost("/pcnk/seats/hold")]
    [EmptyResponse]
    public static PcNkSeatHeld HoldByBody(HoldPcNkSeat command, [WriteAggregate] PcNkSeat seat)
        => new(command.Holder);

    [WolverinePost("/pcnk/seats/decide")]
    [EmptyResponse]
    [AggregateHandler]
    public static PcNkSeatHeld Decide(HoldPcNkSeatByDecider command, PcNkSeat seat) => new(command.Holder);
}
