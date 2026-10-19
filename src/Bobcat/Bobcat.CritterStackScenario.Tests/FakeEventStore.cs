using System.Diagnostics;
using System.Diagnostics.Metrics;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Daemon;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Bobcat.CritterStack.ScenarioTests;

/// <summary>
/// The smallest concrete <see cref="IEventStore"/> — every abstract member stubbed — so a test can
/// shape a store's <i>public surface</i> (an <c>Advanced</c> property, say) the way a reflective
/// convention sees it. NSubstitute proxies cannot grow extra public members, which is why this is a
/// class rather than a substitute.
/// </summary>
public class FakeEventStore : IEventStore
{
    public FakeEventStore(string name = "fake", string type = "fake")
    {
        Identity = new EventStoreIdentity(name, type);
    }

    public Uri Subject { get; } = new("fake://store");
    public Meter Meter { get; } = new("Bobcat.CritterStack.Tests.Fake");
    public ActivitySource ActivitySource { get; } = new("Bobcat.CritterStack.Tests.Fake");
    public string MetricsPrefix => "fake";
    public DatabaseCardinality DatabaseCardinality => DatabaseCardinality.Single;
    public bool HasMultipleTenants => false;
    public EventStoreIdentity Identity { get; }

    public IReadOnlyList<IEventDatabase> Databases { get; set; } = [];
    public IReadOnlyEventStore ReadOnlyView { get; set; } = Substitute.For<IReadOnlyEventStore>();

    /// <summary>What <see cref="TryCreateUsage"/> describes the store as (bobcat#504); null by default.</summary>
    public EventStoreUsage? Usage { get; set; }

    /// <summary>How many times the store was asked to describe itself.</summary>
    public int UsageReads { get; private set; }

    public Task<EventStoreUsage?> TryCreateUsage(CancellationToken token)
    {
        UsageReads++;
        return Task.FromResult(Usage);
    }

    public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(string? tenantIdOrDatabaseIdentifier = null, ILogger? logger = null)
        => throw new NotSupportedException();

    public ValueTask<IProjectionDaemon> BuildProjectionDaemonAsync(DatabaseId id) => throw new NotSupportedException();

    public ValueTask<IReadOnlyList<IEventDatabase>> AllDatabases() => ValueTask.FromResult(Databases);

    public IReadOnlyEventStore OpenReadOnlyEventStore() => ReadOnlyView;

    public Task CompactStreamAsync(Guid streamId, CancellationToken token = default) => Task.CompletedTask;
    public Task CompactStreamAsync(string streamKey, CancellationToken token = default) => Task.CompletedTask;
}
