using System.Collections.Concurrent;
using System.Diagnostics;
using IntegrationTests;
using JasperFx;
using JasperFx.CodeGeneration;
using JasperFx.Core.Reflection;
using JasperFx.Events;
using JasperFx.Events.Aggregation;
using JasperFx.Events.Projections;
using JasperFx.Resources;
using Marten;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Marten;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Util;

namespace MartenTests.AggregateHandlerWorkflow;

/// <summary>
///     GH-5948: <c>[AggregateHandler]</c>, <c>[ConsistentAggregateHandler]</c> and <c>[DeciderFunction]</c> load by
///     natural key, found on the command the way <c>[WriteAggregate]</c> finds it. Also the telemetry of a load by
///     natural key (GH-5947, D9) and changing a natural key (the docs' "Changing a natural key").
/// </summary>
public class natural_key_decider_workflow : PostgresqlContext, IAsyncLifetime
{
    private readonly ITestOutputHelper _output;
    private readonly ConcurrentQueue<Activity> _stopped = new();
    private ActivityListener _listener = null!;
    private IHost _host = null!;
    private IDocumentStore _store = null!;

    public natural_key_decider_workflow(ITestOutputHelper output)
    {
        _output = output;
    }

    public async ValueTask InitializeAsync()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Wolverine",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _stopped.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);

        _host = await WolverineHost.ForAsync(opts =>
        {
            opts.Services.AddMarten(m =>
                {
                    m.Connection(Servers.PostgresConnectionString);
                    m.DatabaseSchemaName = "nk_decider";
                    m.Projections.Snapshot<NkTicket>(SnapshotLifecycle.Inline);
                })
                .UseLightweightSessions()
                .IntegrateWithWolverine();

            opts.Discovery.DisableConventionalDiscovery()
                .IncludeType(typeof(NkTicketHandler))
                .IncludeType(typeof(NkTicketOtherHandlers))
                .IncludeType(typeof(NkTicketConsistentHandler))
                .IncludeType(typeof(NkTicketExclusiveHandler))
                .IncludeType(typeof(NkTicketDecider))
                .IncludeType(typeof(NkTicketRenumberHandler));
            opts.Durability.Mode = DurabilityMode.Solo;

            opts.Services.AddResourceSetupOnStartup();
            opts.CodeGeneration.TypeLoadMode = TypeLoadMode.Auto;
        });

        _store = _host.Services.GetRequiredService<IDocumentStore>();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // A natural key names exactly one stream, and the lookup table outlives the run
    private static NkTicketNumber uniqueNumber() => new("T-" + Guid.NewGuid().ToString("N"));

    private async Task<(Guid StreamId, NkTicketNumber Number)> openTicket()
    {
        var streamId = Guid.NewGuid();
        var number = uniqueNumber();

        await using var session = _store.LightweightSession();
        session.Events.StartStream<NkTicket>(streamId, new NkTicketOpened(number));
        await session.SaveChangesAsync();

        return (streamId, number);
    }

    private async Task<NkTicket> loadTicket(Guid streamId)
    {
        await using var query = _store.QuerySession();
        return (await query.LoadAsync<NkTicket>(streamId)).ShouldNotBeNull();
    }

    private string sourceFor<T>()
    {
        _host.GetRuntime().Handlers.HandlerFor<T>();
        var code = _host.GetRuntime().Handlers.ChainFor<T>()!.SourceCode!;
        _output.WriteLine(code);
        return code;
    }

    private static string fetchFor(string method = "FetchForWriting")
        => $"{method}<{typeof(NkTicket).FullNameInCode()}, {typeof(NkTicketNumber).FullNameInCode()}>(";

    [Fact]
    public async Task aggregate_handler_loads_by_the_natural_key_on_the_command()
    {
        var (streamId, number) = await openTicket();

        await _host.InvokeMessageAndWaitAsync(new NoteNkTicket(number, "First"));

        sourceFor<NoteNkTicket>().ShouldContain(fetchFor());
        (await loadTicket(streamId)).Notes.ShouldBe(1);
    }

    [Fact]
    public async Task the_stream_id_wins_over_a_natural_key()
    {
        var (streamId, _) = await openTicket();
        var (_, other) = await openTicket();

        await _host.InvokeMessageAndWaitAsync(new NoteNkTicketById(streamId, other, "By id"));

        sourceFor<NoteNkTicketById>().ShouldNotContain(fetchFor());
        (await loadTicket(streamId)).Notes.ShouldBe(1);
    }

    [Fact]
    public async Task a_stale_version_is_refused()
    {
        var (streamId, number) = await openTicket();

        await _host.InvokeMessageAndWaitAsync(new NoteNkTicketAtVersion(number, "Current", 1));
        (await loadTicket(streamId)).Notes.ShouldBe(1);

        await Should.ThrowAsync<ConcurrencyException>(() =>
            _host.InvokeMessageAndWaitAsync(new NoteNkTicketAtVersion(number, "Stale", 1)));
    }

    [Fact]
    public async Task a_null_key_is_refused_with_the_member_as_the_param_name()
    {
        var ex = await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _host.InvokeMessageAndWaitAsync(new NoteNkTicketMaybe(null, "Nobody")));

        ex.ParamName.ShouldBe(nameof(NoteNkTicketMaybe.Number));
    }

    [Fact]
    public async Task consistent_aggregate_handler_by_natural_key()
    {
        var (streamId, number) = await openTicket();

        await _host.InvokeMessageAndWaitAsync(new ConfirmNkTicket(number));

        var code = sourceFor<ConfirmNkTicket>();
        code.ShouldContain(fetchFor());
        code.ShouldContain("AlwaysEnforceConsistency = true");
        (await loadTicket(streamId)).Notes.ShouldBe(0);
    }

    [Fact]
    public async Task exclusive_aggregate_handler_by_natural_key_serializes_writers()
    {
        var (streamId, number) = await openTicket();

        // Not tracked: one tracked session at a time per host
        await Task.WhenAll(Enumerable.Range(0, 5)
            .Select(i => _host.MessageBus().InvokeAsync(new NoteNkTicketExclusively(number, $"Note {i}"),
                TestContext.Current.CancellationToken)));

        sourceFor<NoteNkTicketExclusively>().ShouldContain(fetchFor("FetchForExclusiveWriting"));
        (await loadTicket(streamId)).Notes.ShouldBe(5);
    }

    [Fact]
    public async Task decider_function_by_natural_key()
    {
        var (streamId, number) = await openTicket();

        await _host.InvokeMessageAndWaitAsync(new TagNkTicket(number, "urgent"));

        sourceFor<TagNkTicket>().ShouldContain(fetchFor());
        (await loadTicket(streamId)).Tags.ShouldBe(["urgent"]);
    }

    // GH-5947 (D9): the key up front, then the stream's real id once it's loaded
    [Fact]
    public async Task a_load_by_natural_key_tags_the_key_and_then_the_stream_id()
    {
        var (streamId, number) = await openTicket();

        await _host.TrackActivity().SendMessageAndWaitAsync(new NoteNkTicket(number, "Traced"));

        var span = _stopped
            .Where(x => x.OperationName == typeof(NoteNkTicket).ToMessageTypeName())
            .ShouldHaveSingleItem();

        span.GetTagItem(WolverineTracing.StreamNaturalKey).ShouldBe(number);
        span.GetTagItem(WolverineTracing.StreamId).ShouldBe(streamId);
        span.GetTagItem(WolverineTracing.StreamType).ShouldBe(typeof(NkTicket).FullName);
    }

    [Fact]
    public async Task an_unknown_natural_key_tags_no_stream_id()
    {
        await _host.TrackActivity().SendMessageAndWaitAsync(new NoteNkTicketIfFound(uniqueNumber(), "Nobody"));

        var span = _stopped
            .Where(x => x.OperationName == typeof(NoteNkTicketIfFound).ToMessageTypeName())
            .ShouldHaveSingleItem();

        span.GetTagItem(WolverineTracing.StreamNaturalKey).ShouldNotBeNull();
        span.GetTagItem(WolverineTracing.StreamId).ShouldBeNull();
    }

    [Fact]
    public async Task changing_a_natural_key()
    {
        var (streamId, number) = await openTicket();
        var newNumber = uniqueNumber();

        var renumbered = await _host.MessageBus().InvokeAsync<NkTicket>(new RenumberNkTicket(number, newNumber),
            TestContext.Current.CancellationToken);
        renumbered.Number.ShouldBe(newNumber);

        // The new key finds the ticket, and the old one doesn't
        await _host.InvokeMessageAndWaitAsync(new NoteNkTicket(newNumber, "Under the new number"));
        (await loadTicket(streamId)).Notes.ShouldBe(1);

        await using var session = _store.LightweightSession();
        (await session.Events.FetchLatest<NkTicket, NkTicketNumber>(newNumber, TestContext.Current.CancellationToken))
            .ShouldNotBeNull().Id.ShouldBe(streamId);
        (await session.Events.FetchLatest<NkTicket, NkTicketNumber>(number, TestContext.Current.CancellationToken))
            .ShouldBeNull();
    }
}

public record NkTicketNumber(string Value);

public class NkTicket
{
    public Guid Id { get; set; }

    [NaturalKey]
    public NkTicketNumber Number { get; set; } = null!;

    public int Notes { get; set; }
    public List<string> Tags { get; set; } = [];

    [NaturalKeySource]
    public void Apply(NkTicketOpened e) => Number = e.Number;

    [NaturalKeySource]
    public void Apply(NkTicketRenumbered e) => Number = e.NewNumber;

    public void Apply(NkTicketNoted e) => Notes++;

    public void Apply(NkTicketTagged e) => Tags.Add(e.Tag);
}

public record NkTicketOpened(NkTicketNumber Number);
public record NkTicketNoted(string Text);
public record NkTicketTagged(string Tag);
public record NkTicketRenumbered(NkTicketNumber NewNumber);

#region sample_wolverine_marten_aggregate_handler_by_natural_key
// No stream id on the command, so the ticket is loaded by its natural key
public record NoteNkTicket(NkTicketNumber Number, string Text);

[AggregateHandler]
public static class NkTicketHandler
{
    public static NkTicketNoted Handle(NoteNkTicket command, NkTicket ticket) => new(command.Text);
}
#endregion

[AggregateHandler]
public static class NkTicketOtherHandlers
{
    public static NkTicketNoted Handle(NoteNkTicketById command, NkTicket ticket) => new(command.Text);

    public static NkTicketNoted Handle(NoteNkTicketAtVersion command, NkTicket ticket) => new(command.Text);

    public static NkTicketNoted Handle(NoteNkTicketMaybe command, NkTicket ticket) => new(command.Text);

    public static IEnumerable<object> Handle(NoteNkTicketIfFound command, NkTicket? ticket)
    {
        if (ticket != null) yield return new NkTicketNoted(command.Text);
    }
}

public record NoteNkTicketById(Guid NkTicketId, NkTicketNumber Number, string Text);

public record NoteNkTicketAtVersion(NkTicketNumber Number, string Text, long Version);

public record NoteNkTicketMaybe(NkTicketNumber? Number, string Text);

public record NoteNkTicketIfFound(NkTicketNumber Number, string Text);

public record ConfirmNkTicket(NkTicketNumber Number);

[ConsistentAggregateHandler]
public static class NkTicketConsistentHandler
{
    // Nothing to append, but the version is still checked at commit
    public static IEnumerable<object> Handle(ConfirmNkTicket command, NkTicket ticket) => [];
}

public record NoteNkTicketExclusively(NkTicketNumber Number, string Text);

[AggregateHandler(Wolverine.Marten.ConcurrencyStyle.Exclusive)]
public static class NkTicketExclusiveHandler
{
    public static NkTicketNoted Handle(NoteNkTicketExclusively command, NkTicket ticket) => new(command.Text);
}

public record TagNkTicket(NkTicketNumber Number, string Tag);

[DeciderFunction]
public static class NkTicketDecider
{
    public static NkTicketTagged Handle(TagNkTicket command, NkTicket ticket) => new(command.Tag);
}

#region sample_wolverine_marten_changing_a_natural_key
// The command carries the current number and the new one, so the attribute names the one to load by
public record RenumberNkTicket(NkTicketNumber Number, NkTicketNumber NewNumber);

public static class NkTicketRenumberHandler
{
    // NkTicketRenumbered is a [NaturalKeySource] on NkTicket, so appending it moves the ticket to the new number.
    // UpdatedAggregate returns the ticket as it is after the event.
    public static (UpdatedAggregate, NkTicketRenumbered) Handle(RenumberNkTicket command,
        [WriteAggregate<NkTicketNumber>(nameof(RenumberNkTicket.Number))] NkTicket ticket)
        => (new UpdatedAggregate(), new NkTicketRenumbered(command.NewNumber));
}
#endregion
