using System.Collections.Concurrent;
using JasperFx.Core;
using JasperFx.Core.Reflection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine.Configuration;
using Wolverine.Configuration.Capabilities;
using Wolverine.Persistence.Durability;
using Wolverine.RDBMS;
using Wolverine.Runtime;
using Wolverine.Tracking;
using Wolverine.Transports;
using Wolverine.Transports.Sending;
using Xunit;

namespace Wolverine.ComplianceTests;

/// <summary>
/// GH-5050. How a <see cref="DurabilityMode.ServerlessWithOutbox" /> durability pass copes with outbox rows that keep
/// failing to send, against a real database. A failed send restarts the row's stale clock, so the row goes to the
/// back of the stale order and is not tried again for OutboxStaleTime: rows that keep failing can neither starve the
/// healthy rows behind them nor keep a drain loop on <see cref="DurabilityPassReport.HasMoreWork" /> going forever.
/// A pass that skipped a store because another pass held its lock says there is more work.
/// </summary>
public abstract class DurabilityPassRecoveryCompliance : IAsyncLifetime
{
    protected const string StepOutgoing = "Recover stale outgoing messages";

    private readonly List<IHost> _hosts = new();

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    protected IHost theHost = null!;

    /// <summary>
    /// The schema (or table prefix) this suite owns
    /// </summary>
    protected abstract string SchemaName { get; }

    protected abstract void ConfigurePersistence(WolverineOptions opts, string schemaName);

    protected virtual string Quote(string column) => $"\"{column}\"";

    /// <summary>
    /// The value a test writes to the timestamp column, in the form the store compares it in
    /// </summary>
    protected virtual object TimestampValue(DateTimeOffset value) => value;

    public async ValueTask InitializeAsync()
    {
        ControlledTransport.Reset();

        theHost = await BuildHostAsync();
        await theHost.RebuildAllEnvelopeStorageAsync();
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var host in _hosts)
        {
            try
            {
                await host.StopAsync();
            }
            catch
            {
                // best effort
            }

            host.Dispose();
        }
    }

    protected async Task<IHost> BuildHostAsync()
    {
        var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.ServerlessWithOutbox;
                opts.Discovery.DisableConventionalDiscovery();
                opts.Transports.GetOrCreate<ControlledTransport>();
                ConfigurePersistence(opts, SchemaName);
            }).StartAsync();

        _hosts.Add(host);
        return host;
    }

    private IMessageDatabase database => theHost.GetRuntime().Storage.As<IMessageDatabase>();

    private async Task<int> executeAsync(string sql, params (string Name, object Value)[] parameters)
    {
        await using var conn = await database.DataSource.OpenConnectionAsync(Token);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            var p = cmd.CreateParameter();
            p.ParameterName = name;
            p.Value = value;
            cmd.Parameters.Add(p);
        }

        return await cmd.ExecuteNonQueryAsync(Token);
    }

    private string outgoing => database.TableNameFor(DatabaseConstants.OutgoingTable);

    /// <summary>
    /// Store <paramref name="count" /> owner-0 outbox rows for ctl://{destination} whose stale clock started
    /// <paramref name="age" /> ago, the way a request that failed to send them inline leaves them
    /// </summary>
    protected async Task<Envelope[]> StoreStaleOutgoingAsync(string destination, int count, TimeSpan age)
    {
        var uri = $"ctl://{destination}".ToUri();
        var envelopes = Enumerable.Range(0, count).Select(_ =>
        {
            var envelope = ObjectMother.Envelope();
            envelope.Destination = uri;
            envelope.DeliverBy = null;
            return envelope;
        }).ToArray();

        foreach (var chunk in envelopes.Chunk(100))
        {
            await theHost.GetRuntime().Storage.Outbox.StoreOutgoingAsync(chunk, 0);
        }

        await MakeStaleAsync(destination, age);

        return envelopes;
    }

    protected Task MakeStaleAsync(string destination, TimeSpan age)
    {
        return executeAsync(
            $"update {outgoing} set {Quote(DatabaseConstants.Timestamp)} = @time where {DatabaseConstants.Destination} = @destination",
            ("time", TimestampValue(DateTimeOffset.UtcNow.Subtract(age))), ("destination", $"ctl://{destination}".ToUri().ToString()));
    }

    protected Task<DurabilityPassReport> RunPassAsync()
    {
        return theHost.RunDurabilityPassAsync(new DurabilityPassOptions
        {
            MaxDuration = 1.Minutes(), BatchSize = 100
        }, Token);
    }

    protected static DurabilityPassStepReport StepOf(DurabilityPassReport report) =>
        report.Stores.SelectMany(x => x.Steps).Single(x => x.StepName == StepOutgoing);

    [Fact]
    public async Task healthy_rows_behind_more_failing_rows_than_a_pass_excludes_are_sent_by_the_next_pass()
    {
        ControlledTransport.Behavior("down").Fail = true;

        // More failing rows than DurabilityPassContext.MaxExcludedIds, all older than the healthy ones
        await StoreStaleOutgoingAsync("down", 600, 2.Hours());
        var healthy = await StoreStaleOutgoingAsync("healthy", 10, 1.Hours());

        var first = await RunPassAsync();
        var second = await RunPassAsync();

        // Before GH-5050 every pass started again from the same 600 failing rows and never reached these
        healthy.ShouldAllBe(x => ControlledTransport.Behavior("healthy").Sent.ContainsKey(x.Id),
            $"first pass: {first}; second pass: {second}");

        // The failing rows were each tried once: their stale clocks were restarted, so the second pass left them
        ControlledTransport.Behavior("down").Attempts.ShouldBe(600);
        second.HasMoreWork.ShouldBeFalse();
    }

    [Fact]
    public async Task a_row_whose_send_failed_is_not_tried_again_until_its_stale_clock_runs_out_again()
    {
        ControlledTransport.Behavior("down").Fail = true;
        await StoreStaleOutgoingAsync("down", 1, 2.Hours());

        var first = await RunPassAsync();
        StepOf(first).Result.OutgoingFailed.ShouldBe(1);

        // Straight away: the failed row's stale clock started again, so it is not stale yet
        var second = await RunPassAsync();
        ControlledTransport.Behavior("down").Attempts.ShouldBe(1);
        second.HasMoreWork.ShouldBeFalse();

        // Once its clock runs out again it is tried again, and sent once the destination is back
        ControlledTransport.Behavior("down").Fail = false;
        await MakeStaleAsync("down", 2.Hours());
        var third = await RunPassAsync();

        third.OutgoingSent.ShouldBe(1);
        ControlledTransport.Behavior("down").Sent.Count.ShouldBe(1);
    }

    [Fact]
    public async Task a_drain_loop_on_HasMoreWork_ends_when_only_failing_rows_are_left()
    {
        // More than DurabilityPassContext.MaxExcludedIds, so a pass stops excluding them part way through
        ControlledTransport.Behavior("down").Fail = true;
        await StoreStaleOutgoingAsync("down", 600, 2.Hours());

        var passes = 0;
        DurabilityPassReport report;
        do
        {
            report = await RunPassAsync();
            passes++;
        } while (report.HasMoreWork && passes < 10);

        report.HasMoreWork.ShouldBeFalse(
            $"after {passes} passes HasMoreWork is still true: {report}");
        passes.ShouldBe(1);
    }

    [Fact]
    public async Task a_pass_that_skipped_a_store_held_by_another_pass_reports_that_work_is_left()
    {
        await StoreStaleOutgoingAsync("healthy", 5, 2.Hours());

        var passStore = theHost.GetRuntime().Storage.As<IMessageStoreWithDurabilityPass>();
        await using var held = await passStore.TryAcquireDurabilityPassLockAsync(Token);
        held.ShouldNotBeNull();

        var other = await BuildHostAsync();
        var report = await other.RunDurabilityPassAsync(30.Seconds(), Token);

        report.SkippedStores.Count.ShouldBe(1);
        report.Remaining.ShouldBe(0);
        report.HasMoreWork.ShouldBeTrue();
    }
}

public class ControlledBehavior
{
    private int _attempts;

    public bool Fail { get; set; }
    public ConcurrentDictionary<Guid, int> Sent { get; } = new();
    public int Attempts => Volatile.Read(ref _attempts);

    internal ValueTask SendAsync(Envelope envelope)
    {
        Interlocked.Increment(ref _attempts);
        if (Fail) throw new InvalidOperationException($"Controlled send to {envelope.Destination} failed");

        Sent.AddOrUpdate(envelope.Id, 1, (_, c) => c + 1);
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// ctl://{name}: a sender whose behavior (fail or succeed) is set per name and shared by every host in the process
/// </summary>
public class ControlledTransport : TransportBase<ControlledEndpoint>
{
    private static ConcurrentDictionary<string, ControlledBehavior> _behaviors = new();

    public static ControlledBehavior Behavior(string name) => _behaviors.GetOrAdd(name, _ => new ControlledBehavior());

    public static void Reset()
    {
        _behaviors = new ConcurrentDictionary<string, ControlledBehavior>();
    }

    public ControlledTransport() : base("ctl", "Controlled test transport", [])
    {
        Endpoints = new LightweightCache<string, ControlledEndpoint>(name => new ControlledEndpoint(name));
    }

    public new LightweightCache<string, ControlledEndpoint> Endpoints { get; }

    public override bool TryBuildBrokerUsage(out BrokerDescription description)
    {
        description = default!;
        return false;
    }

    protected override IEnumerable<ControlledEndpoint> endpoints() => Endpoints;

    protected override ControlledEndpoint findEndpointByUri(Uri uri) => Endpoints[uri.Host];
}

public class ControlledEndpoint : Endpoint, ISender
{
    public ControlledEndpoint(string name) : base($"ctl://{name}".ToUri(), EndpointRole.Application)
    {
        EndpointName = name;
    }

    // Senders inline, so a pass sends straight through
    protected override bool supportsMode(EndpointMode mode) => mode == EndpointMode.Inline;

    public override ValueTask<IListener> BuildListenerAsync(IWolverineRuntime runtime, IReceiver receiver)
    {
        throw new NotSupportedException();
    }

    protected override ISender CreateSender(IWolverineRuntime runtime) => this;

    public bool SupportsNativeScheduledSend => false;
    public Uri Destination => Uri;
    public Task<bool> PingAsync() => Task.FromResult(true);

    public ValueTask SendAsync(Envelope envelope) => ControlledTransport.Behavior(EndpointName).SendAsync(envelope);
}
