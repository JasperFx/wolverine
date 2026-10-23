using System.Text.Json;
using JasperFx.Descriptors;
using JasperFx.Events;
using JasperFx.Events.Descriptors;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Shouldly;
using Wolverine;
using Wolverine.Configuration.Capabilities;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Acceptance;

// GH-5162: one failing store used to empty out its whole ServiceCapabilities section, and the snapshot then
// reported that section as empty rather than failed. Each store is now read on its own, and anything that
// could not be read is listed on ServiceCapabilities.FailedSections.
public class service_capabilities_failed_sections_5162
{
    private static readonly Uri HealthyEventStoreUri = new("fake://events/healthy");
    private static readonly Uri BrokenEventStoreUri = new("fake://events/broken");
    private static readonly Uri HealthyDocumentStoreUri = new("fake://documents/healthy");
    private static readonly Uri BrokenDocumentStoreUri = new("fake://documents/broken");

    [Fact]
    public async Task one_failing_event_store_drops_only_itself()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(healthyEventStore("healthy", HealthyEventStoreUri));
                opts.Services.AddSingleton(brokenEventStore("broken", BrokenEventStoreUri));
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var capabilities =
            await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, TestContext.Current.CancellationToken);

        // The healthy store is still described...
        capabilities.EventStores.ShouldHaveSingleItem().SubjectUri.ShouldBe(HealthyEventStoreUri);

        // ...and the broken one is reported as failed instead of silently missing
        var failure = capabilities.FailedSections.ShouldHaveSingleItem();
        failure.Section.ShouldBe(nameof(ServiceCapabilities.EventStores));
        failure.Subject.ShouldBe(BrokenEventStoreUri.ToString());
        failure.ExceptionType.ShouldBe(typeof(TimeoutException).FullName);
    }

    [Fact]
    public async Task one_failing_document_store_drops_only_itself()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Services.AddSingleton(healthyDocumentStore(HealthyDocumentStoreUri));
                opts.Services.AddSingleton(brokenDocumentStore(BrokenDocumentStoreUri));
            })
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var capabilities =
            await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, TestContext.Current.CancellationToken);

        capabilities.DocumentStores.ShouldHaveSingleItem().SubjectUri.ShouldBe(HealthyDocumentStoreUri);

        var failure = capabilities.FailedSections.ShouldHaveSingleItem();
        failure.Section.ShouldBe(nameof(ServiceCapabilities.DocumentStores));
        failure.Subject.ShouldBe(BrokenDocumentStoreUri.ToString());
        failure.ExceptionType.ShouldBe(typeof(TimeoutException).FullName);
    }

    [Fact]
    public async Task an_empty_section_with_no_failures_really_is_empty()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine()
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        var capabilities =
            await ServiceCapabilities.ReadFrom(host.GetRuntime(), null, TestContext.Current.CancellationToken);

        capabilities.EventStores.ShouldBeEmpty();
        capabilities.DocumentStores.ShouldBeEmpty();
        capabilities.FailedSections.ShouldBeEmpty();
    }

    [Fact]
    public async Task cancellation_while_reading_a_store_is_not_recorded_as_a_failure()
    {
        using var cancellation = new CancellationTokenSource();

        var store = Substitute.For<IEventStore>();
        store.Subject.Returns(BrokenEventStoreUri);
        store.TryCreateUsage(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            // The caller gives up while this store is still being read
            await cancellation.CancelAsync();
            return await Task.FromCanceled<EventStoreUsage?>(cancellation.Token);
        });

        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.Services.AddSingleton(store))
            .StartAsync(cancellationToken: TestContext.Current.CancellationToken);

        await Should.ThrowAsync<OperationCanceledException>(() =>
            ServiceCapabilities.ReadFrom(host.GetRuntime(), null, cancellation.Token));
    }

    [Fact]
    public void failed_sections_survive_a_json_round_trip()
    {
        var capabilities = new ServiceCapabilities();
        capabilities.FailedSections.Add(new CapabilitySectionFailure
        {
            Section = nameof(ServiceCapabilities.EventStores),
            Subject = BrokenEventStoreUri.ToString(),
            ExceptionType = typeof(TimeoutException).FullName!
        });

        var json = JsonSerializer.Serialize(capabilities);
        var copy = JsonSerializer.Deserialize<ServiceCapabilities>(json)!;

        var failure = copy.FailedSections.ShouldHaveSingleItem();
        failure.Section.ShouldBe(nameof(ServiceCapabilities.EventStores));
        failure.Subject.ShouldBe(BrokenEventStoreUri.ToString());
        failure.ExceptionType.ShouldBe(typeof(TimeoutException).FullName);
    }

    [Fact]
    public void a_snapshot_from_before_failed_sections_still_deserializes()
    {
        // What an older Wolverine node sends: no FailedSections property at all
        var json = JsonSerializer.Serialize(new ServiceCapabilities());
        using var document = JsonDocument.Parse(json);
        var withoutFailedSections = document.RootElement.EnumerateObject()
            .Where(x => x.Name != nameof(ServiceCapabilities.FailedSections))
            .ToDictionary(x => x.Name, x => x.Value);

        json.ShouldContain(nameof(ServiceCapabilities.FailedSections));

        var copy = JsonSerializer.Deserialize<ServiceCapabilities>(JsonSerializer.Serialize(withoutFailedSections))!;

        copy.FailedSections.ShouldNotBeNull();
        copy.FailedSections.ShouldBeEmpty();
    }

    private static IEventStore healthyEventStore(string name, Uri subject)
    {
        var store = Substitute.For<IEventStore>();
        store.Subject.Returns(subject);
        store.Identity.Returns(new EventStoreIdentity(name, "fake"));
        store.TryCreateUsage(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<EventStoreUsage?>(new EventStoreUsage(subject, store)));
        return store;
    }

    private static IEventStore brokenEventStore(string name, Uri subject)
    {
        // Stands in for the field case behind GH-5162: an event store whose usage read has to query the
        // database (Fisher reads max(seq_id)) and that query fails under load
        var store = Substitute.For<IEventStore>();
        store.Subject.Returns(subject);
        store.Identity.Returns(new EventStoreIdentity(name, "fake"));
        store.TryCreateUsage(Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("Simulated event store usage failure for GH-5162"));
        return store;
    }

    private static IDocumentStoreUsageSource healthyDocumentStore(Uri subject)
    {
        var store = Substitute.For<IDocumentStoreUsageSource>();
        store.Subject.Returns(subject);
        store.TryCreateUsage(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<DocumentStoreUsage?>(new DocumentStoreUsage(subject, store)));
        return store;
    }

    private static IDocumentStoreUsageSource brokenDocumentStore(Uri subject)
    {
        var store = Substitute.For<IDocumentStoreUsageSource>();
        store.Subject.Returns(subject);
        store.TryCreateUsage(Arg.Any<CancellationToken>())
            .ThrowsAsync(new TimeoutException("Simulated document store usage failure for GH-5162"));
        return store;
    }
}
