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
using Wolverine.Http.Fisher;
using Wolverine.Fisher;
using Wolverine.Runtime;
using Wolverine.Tracking;
using JasperFx.CodeGeneration;
using Fisher;

namespace FisherTests;

/// <summary>
///     GH-5901, GH-5947 and GH-5948 on Fisher: reads by natural key, the generic <c>[WriteAggregate&lt;TKey&gt;]</c>,
///     <c>[ReadAggregate&lt;TKey&gt;]</c> and <c>[Aggregate&lt;TKey&gt;]</c>, the decider workflow by natural key, and
///     the HTTP status codes, against Fisher's primary store. The Polecat and Marten suites cover the same ground.
/// </summary>
public class natural_keys_in_7_0 : IAsyncLifetime
{
    private IAlbaHost theHost = null!;
    private FisherTestDatabase theDatabase = null!;

    public async ValueTask InitializeAsync()
    {
        theDatabase = Servers.CreateDatabase("nk_7_0");

        var builder = WebApplication.CreateBuilder([]);

        builder.Host.UseWolverine(opts =>
        {
            opts.Durability.Mode = DurabilityMode.Solo;
            opts.Policies.AutoApplyTransactions();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(FiNkSeatHandlers))
                .IncludeType(typeof(FiNkSeatOtherHandlers))
                .IncludeType(typeof(FiNkSeatDecider));
            opts.Discovery.IncludeAssembly(typeof(natural_keys_in_7_0).Assembly);

            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        builder.Services.AddFisher(m =>
            {
                m.Connection(theDatabase.ConnectionString);
                m.AutoCreateSchemaObjects = AutoCreate.All;
                m.Projections.Snapshot<FiNkSeat>(SnapshotLifecycle.Inline);
            })
            .ApplyAllDatabaseChangesOnStartup()
            .IntegrateWithWolverine();

        builder.Services.AddWolverineHttp();

        theHost = await AlbaHost.For(builder, app => app.MapWolverineEndpoints(opts =>
            opts.CustomizeHttpEndpointDiscovery(q =>
                q.Excludes.WithCondition("Not a natural key endpoint", type => type != typeof(FiNkSeatEndpoints)))));
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.DisposeAsync();
        theDatabase.Dispose();
    }

    private IDocumentStore theStore => theHost.Services.GetRequiredService<IDocumentStore>();

    // A natural key names exactly one stream, and the lookup table outlives the run
    private static FiNkSeatCode uniqueCode() => new("S-" + Guid.NewGuid().ToString("N"));

    private async Task<(Guid StreamId, FiNkSeatCode Code)> addSeat()
    {
        var streamId = Guid.NewGuid();
        var code = uniqueCode();

        await using var session = theStore.LightweightSession();
        session.Events.StartStream<FiNkSeat>(streamId, new FiNkSeatAdded(code));
        await session.SaveChangesAsync(TestContext.Current.CancellationToken);

        return (streamId, code);
    }

    private async Task<FiNkSeat> loadSeat(Guid streamId)
    {
        await using var session = theStore.LightweightSession();
        return (await session.LoadAsync<FiNkSeat>(streamId, TestContext.Current.CancellationToken)).ShouldNotBeNull();
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

        await theHost.InvokeMessageAndWaitAsync(new HoldFiNkSeat(code, "Alice"));

        (await loadSeat(streamId)).Holder.ShouldBe("Alice");
    }

    [Fact]
    public async Task read_aggregate_by_the_named_key_type()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new ViewFiNkSeat(code));

        FiNkSeatHandlers.LastSeat.ShouldNotBeNull().Id.ShouldBe(streamId);
        sourceFor<ViewFiNkSeat>().ShouldContain(
            $"FetchLatest<{typeof(FiNkSeat).FullName}, {typeof(FiNkSeatCode).FullName}>");
    }

    [Fact]
    public async Task read_aggregate_by_an_inferred_key()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new PeekFiNkSeat(code));

        FiNkSeatHandlers.LastSeat.ShouldNotBeNull().Id.ShouldBe(streamId);
    }

    [Fact]
    public async Task an_optional_read_by_a_null_key_finds_nothing()
    {
        await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new MaybeFiNkSeat(null));

        FiNkSeatHandlers.LastMaybe.ShouldBeNull();
    }

    [Fact]
    public async Task the_decider_workflow_loads_by_the_natural_key()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new HoldFiNkSeatByDecider(code, "Bob"));

        (await loadSeat(streamId)).Holder.ShouldBe("Bob");
    }

    [Fact]
    public async Task the_decider_workflow_by_key_at_a_version()
    {
        var (streamId, code) = await addSeat();

        await theHost.InvokeMessageAndWaitAsync(new HoldFiNkSeatAtVersion(code, "Current", 1));
        (await loadSeat(streamId)).Holder.ShouldBe("Current");

        // Fisher has no versioned fetch by natural key yet (fisher#688), so the version isn't checked on load
        await theHost.InvokeMessageAndWaitAsync(new HoldFiNkSeatAtVersion(code, "Unchecked", 1));
        (await loadSeat(streamId)).Holder.ShouldBe("Unchecked");
    }

    [Fact]
    public void a_type_argument_and_a_different_natural_key_are_refused()
    {
        var ex = Should.Throw<Exception>(() =>
            theHost.GetRuntime().Handlers.HandlerFor<HoldFiNkSeatTwice>());
        ex.ToString().ShouldContain("names two different natural key types");
    }

    // ---------- over HTTP ----------

    [Fact]
    public async Task aggregate_by_the_named_key_type_in_the_route()
    {
        var (streamId, code) = await addSeat();

        await theHost.Scenario(s =>
        {
            s.Post.Json(new HoldFiNkSeatRequest("Carol")).ToUrl($"/fink/seats/{code.Value}/hold");
            s.StatusCodeShouldBe(204);
        });

        (await loadSeat(streamId)).Holder.ShouldBe("Carol");

        // Fisher's FetchForWriting<T, TKey> throws for a key no stream has. Wolverine.Fisher turns that into the empty
        // stream Marten hands back, so the not-found check answers 404 (GH-5948)
        await theHost.Scenario(s =>
        {
            s.Post.Json(new HoldFiNkSeatRequest("Nobody")).ToUrl("/fink/seats/S-NONE/hold");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task an_unknown_key_on_a_write_finds_no_aggregate()
    {
        // The required [WriteAggregate] stops the handler, as on Marten, rather than the load throwing
        await theHost.InvokeMessageAndWaitAsync(new HoldFiNkSeat(uniqueCode(), "Nobody"));

        // The decider workflow's aggregate isn't required, so its handler runs with no seat
        await theHost.InvokeMessageAndWaitAsync(new HoldFiNkSeatByDecider(uniqueCode(), "Nobody"));
    }

    [Fact]
    public async Task read_by_the_named_key_type_in_the_route()
    {
        var (streamId, code) = await addSeat();

        var seat = await theHost.GetAsJson<FiNkSeat>($"/fink/seats/{code.Value}");
        seat.ShouldNotBeNull().Id.ShouldBe(streamId);

        await theHost.Scenario(s =>
        {
            s.Get.Url("/fink/seats/S-NONE");
            s.StatusCodeShouldBe(404);
        });
    }

    [Fact]
    public async Task a_write_with_no_key_in_the_request_body_is_a_400()
    {
        var result = await theHost.Scenario(s =>
        {
            s.Post.Json(new HoldFiNkSeat(null!, "Nobody")).ToUrl("/fink/seats/hold");
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
            s.Post.Json(new HoldFiNkSeatByDecider(code, "Dave")).ToUrl("/fink/seats/decide");
            s.StatusCodeShouldBe(204);
        });

        (await loadSeat(streamId)).Holder.ShouldBe("Dave");
    }
}

// A class key binds from the route only through its own TryParse
public record FiNkSeatCode(string Value)
{
    public static bool TryParse(string? value, IFormatProvider? provider, [NotNullWhen(true)] out FiNkSeatCode? code)
    {
        code = string.IsNullOrWhiteSpace(value) ? null : new FiNkSeatCode(value);
        return code != null;
    }
}

public class FiNkSeat
{
    public Guid Id { get; set; }

    [NaturalKey]
    public FiNkSeatCode Code { get; set; } = null!;

    public string? Holder { get; set; }

    [NaturalKeySource]
    public void Apply(FiNkSeatAdded e) => Code = e.Code;

    public void Apply(FiNkSeatHeld e) => Holder = e.Holder;
}

public record FiNkSeatAdded(FiNkSeatCode Code);
public record FiNkSeatHeld(string Holder);

public record HoldFiNkSeat(FiNkSeatCode Code, string Holder);
public record HoldFiNkSeatTwice(FiNkSeatCode Code, string Holder);
public record ViewFiNkSeat(FiNkSeatCode Code);
public record PeekFiNkSeat(FiNkSeatCode Code);
public record MaybeFiNkSeat(FiNkSeatCode? Code);
public record HoldFiNkSeatByDecider(FiNkSeatCode Code, string Holder);
public record HoldFiNkSeatAtVersion(FiNkSeatCode Code, string Holder, long Version);
public record HoldFiNkSeatRequest(string Holder);

#region sample_wolverine_fisher_natural_key_type_argument
public static class FiNkSeatHandlers
{
    public static FiNkSeat? LastSeat;
    public static FiNkSeat? LastMaybe;

    // Name the key type when the message carries more than one key, or to say so up front
    public static FiNkSeatHeld Handle(HoldFiNkSeat command, [WriteAggregate<FiNkSeatCode>] FiNkSeat seat)
        => new(command.Holder);

    // Reads by natural key
    public static void Handle(ViewFiNkSeat query, [ReadAggregate<FiNkSeatCode>] FiNkSeat seat) => LastSeat = seat;
}
#endregion

public static class FiNkSeatOtherHandlers
{
    public static void Handle(PeekFiNkSeat query, [ReadAggregate] FiNkSeat seat)
        => FiNkSeatHandlers.LastSeat = seat;

    public static void Handle(MaybeFiNkSeat query, [ReadAggregate(Required = false)] FiNkSeat? seat)
        => FiNkSeatHandlers.LastMaybe = seat;

#pragma warning disable JFXEVT016, JFXEVT017
    public static FiNkSeatHeld Handle(HoldFiNkSeatTwice command,
        [WriteAggregate<FiNkSeatCode>(NaturalKey = typeof(string))] FiNkSeat seat) => new(command.Holder);
#pragma warning restore JFXEVT016, JFXEVT017
}

[AggregateHandler]
public static class FiNkSeatDecider
{
    public static FiNkSeatHeld Handle(HoldFiNkSeatByDecider command, FiNkSeat seat) => new(command.Holder);

    public static FiNkSeatHeld Handle(HoldFiNkSeatAtVersion command, FiNkSeat seat) => new(command.Holder);
}

public static class FiNkSeatEndpoints
{
    [WolverinePost("/fink/seats/{code}/hold")]
    [EmptyResponse]
    public static FiNkSeatHeld Hold(HoldFiNkSeatRequest request, [Aggregate<FiNkSeatCode>("code")] FiNkSeat seat)
        => new(request.Holder);

    [WolverineGet("/fink/seats/{code}")]
    public static FiNkSeat Get([ReadAggregate<FiNkSeatCode>("code")] FiNkSeat seat) => seat;

    [WolverinePost("/fink/seats/hold")]
    [EmptyResponse]
    public static FiNkSeatHeld HoldByBody(HoldFiNkSeat command, [WriteAggregate] FiNkSeat seat)
        => new(command.Holder);

    [WolverinePost("/fink/seats/decide")]
    [EmptyResponse]
    [AggregateHandler]
    public static FiNkSeatHeld Decide(HoldFiNkSeatByDecider command, FiNkSeat seat) => new(command.Holder);
}
