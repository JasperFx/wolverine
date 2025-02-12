using JasperFx.Core;
using Shouldly;
using Wolverine.ComplianceTests;
using Wolverine;
using Wolverine.Transports;

namespace MartenTests.MultiTenancy;

public class cross_database_message_storage : MultiTenancyContext, IAsyncLifetime
{
    public cross_database_message_storage(MultiTenancyFixture fixture) : base(fixture)
    {
    }

    public async Task InitializeAsync()
    {
        await Stores.Admin.ClearAllAsync();
        await Runtime.DisableAgentsAsync(DateTimeOffset.UtcNow);
    }

    public Task DisposeAsync()
    {
        return Task.CompletedTask;
    }

    [Fact]
    public async Task drain_async_smoke_test()
    {
        await Stores.DrainAsync();
    }

    public Envelope envelopeFor(string tenantId)
    {
        var envelope = ObjectMother.Envelope();
        envelope.TenantId = tenantId;

        return envelope;
    }

    [Fact]
    public async Task store_incoming_with_no_tenant()
    {
        var envelope = envelopeFor(null);
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;

        await Stores.Inbox.StoreIncomingAsync(envelope);

        var envelopes = await Stores.Master.Admin.AllIncomingAsync();
        envelopes.ShouldContain(x => x.Id == envelope.Id);
    }

    [Fact]
    public async Task mark_as_handled_with_no_tenant()
    {
        var envelope = envelopeFor(null);
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;

        await Stores.Inbox.StoreIncomingAsync(envelope);

        await Stores.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var envelopes = await Stores.Master.Admin.AllIncomingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task schedule_with_no_tenant()
    {
        var envelope = envelopeFor(null);
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;

        await Stores.Inbox.StoreIncomingAsync(envelope);
        await Stores.Inbox.ScheduleExecutionAsync(envelope);

        var envelopes = await Stores.Master.Admin.AllIncomingAsync();
        envelopes.ShouldContain(x => x.Id == envelope.Id);
    }

    [Fact]
    public async Task store_incoming_with_a_tenant()
    {
        var envelope = envelopeFor("tenant1");
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;

        await Stores.Inbox.StoreIncomingAsync(envelope);

        var database1 = await Stores.GetDatabaseAsync("tenant1");
        var envelopes = await database1.Admin.AllIncomingAsync();
        envelopes.ShouldContain(x => x.Id == envelope.Id);
    }

    [Fact]
    public async Task mark_incoming_as_handled_with_a_tenant()
    {
        var envelope = envelopeFor("tenant1");
        envelope.Status = EnvelopeStatus.Incoming;

        await Stores.Inbox.StoreIncomingAsync(envelope);

        await Stores.Inbox.MarkIncomingEnvelopeAsHandledAsync(envelope);

        var database1 = await Stores.GetDatabaseAsync("tenant1");
        var envelopes = await database1.Admin.AllIncomingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.Status.ShouldBe(EnvelopeStatus.Handled);
    }

    [Fact]
    public async Task schedule_with_a_tenant()
    {
        var envelope = envelopeFor("tenant1");
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;

        await Stores.Inbox.StoreIncomingAsync(envelope);
        await Stores.Inbox.ScheduleExecutionAsync(envelope);

        var database1 = await Stores.GetDatabaseAsync("tenant1");
        var envelopes = await database1.Admin.AllIncomingAsync();
        envelopes.ShouldContain(x => x.Id == envelope.Id);
    }

    [Fact]
    public async Task store_outgoing_with_no_tenant()
    {
        var envelope = envelopeFor(null);
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;

        await Stores.Outbox.StoreOutgoingAsync(envelope, 5);

        var envelopes = await Stores.Master.Admin.AllOutgoingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.OwnerId.ShouldBe(5);
    }

    [Fact]
    public async Task store_outgoing_with_a_tenant()
    {
        var envelope = envelopeFor("tenant2");
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Outgoing;

        await Stores.Outbox.StoreOutgoingAsync(envelope, 11);

        var database2 = await Stores.GetDatabaseAsync("tenant2");
        var envelopes = await database2.Admin.AllOutgoingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.OwnerId.ShouldBe(11);
    }

    [Fact]
    public async Task increment_incoming_envelope_with_no_tenant()
    {
        var envelope = envelopeFor(null);
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Incoming;
        envelope.Attempts = 3;

        await Stores.Inbox.StoreIncomingAsync(envelope);

        envelope.Attempts = 4;
        await Stores.Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);

        var envelopes = await Stores.Master.Admin.AllIncomingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.Attempts.ShouldBe(4);
    }

    [Fact]
    public async Task increment_incoming_envelope_with_a_tenant()
    {
        var envelope = envelopeFor("tenant1");
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;
        envelope.Attempts = 3;

        await Stores.Inbox.StoreIncomingAsync(envelope);

        envelope.Attempts = 4;
        await Stores.Inbox.IncrementIncomingEnvelopeAttemptsAsync(envelope);

        var database = await Stores.GetDatabaseAsync("tenant1");


        var envelopes = await database.Admin.AllIncomingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.Attempts.ShouldBe(4);
    }

    [Fact]
    public async Task store_many_envelopes_all_with_no_tenant()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        var loaded = await Stores.Master.Admin.AllIncomingAsync();

        loaded.Select(x => x.Id).OrderBy(x => x)
            .ShouldHaveTheSameElementsAs(envelopes.Select(x => x.Id).OrderBy(x => x));

    }

    [Fact]
    public async Task store_many_envelopes_all_with_all_specific_tenant()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        var database = await Stores.GetDatabaseAsync("tenant3");
        var loaded = await database.Admin.AllIncomingAsync();

        loaded.Select(x => x.Id).OrderBy(x => x)
            .ShouldHaveTheSameElementsAs(envelopes.Select(x => x.Id).OrderBy(x => x));

    }

    [Fact]
    public async Task store_many_envelopes_with_mixed_tenants()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        var database1 = await Stores.GetDatabaseAsync("tenant1");
        var count1 = (await database1.Admin.AllIncomingAsync()).Count;
        count1.ShouldBe(envelopes.Count(x => x.TenantId == "tenant1"));

        var database2 = await Stores.GetDatabaseAsync("tenant2");
        var count2 = (await database2.Admin.AllIncomingAsync()).Count;
        count2.ShouldBe(envelopes.Count(x => x.TenantId == "tenant2"));

        var database3 = await Stores.GetDatabaseAsync("tenant3");
        var count3 = (await database3.Admin.AllIncomingAsync()).Count;
        count3.ShouldBe(envelopes.Count(x => x.TenantId == "tenant3"));
    }

    [Fact]
    public async Task all_incoming()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        var all = await Stores.Admin.AllIncomingAsync();
        all.Count.ShouldBe(envelopes.Count);
    }

    [Fact]
    public async Task check_connectivity_smoke_test()
    {
        await Stores.Admin.CheckConnectivityAsync(CancellationToken.None);
    }

    [Fact]
    public async Task all_outgoing()
    {
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor(null), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor(null), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);

        var envelopes = await Stores.Admin.AllOutgoingAsync();

        envelopes.Count.ShouldBe(19);

    }

    [Fact]
    public async Task fetch_counts()
    {
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor(null), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor(null), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);

        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        foreach (var envelope in envelopes)
        {
            envelope.Status = EnvelopeStatus.Incoming;
        }

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        var counts = await Stores.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(envelopes.Count);
        counts.Outgoing.ShouldBe(19);
    }

    [Fact]
    public async Task release_ownership_smoke()
    {
        await Stores.Admin.ReleaseAllOwnershipAsync();
    }

    [Fact]
    public async Task rebuild_smoke()
    {
        await Stores.Admin.RebuildAsync();
    }

    [Fact]
    public async Task clear_all_spans_all_databases()
    {
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor(null), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor(null), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant1"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant2"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);
        await Stores.Outbox.StoreOutgoingAsync(envelopeFor("tenant3"), 3);

        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        foreach (var envelope in envelopes)
        {
            envelope.Status = EnvelopeStatus.Incoming;
        }

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        await Stores.Admin.ClearAllAsync();

        var counts = await Stores.Admin.FetchCountsAsync();

        counts.Incoming.ShouldBe(0);
        counts.Outgoing.ShouldBe(0);
    }

    [Fact]
    public async Task release_ownership_smoke_test()
    {
        await Stores.Inbox.ReleaseIncomingAsync(3);

        await Stores.Inbox.ReleaseIncomingAsync(4, TransportConstants.LocalUri);
    }

    [Fact]
    public async Task delete_outgoing_with_no_tenant()
    {
        var envelope = envelopeFor(null);
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Scheduled;

        await Stores.Outbox.StoreOutgoingAsync(envelope, 5);
        await Stores.Outbox.DeleteOutgoingAsync(envelope);

        var envelopes = await Stores.Master.Admin.AllOutgoingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task delete_outgoing_with_a_tenant()
    {
        var envelope = envelopeFor("tenant2");
        envelope.ScheduleDelay = 5.Minutes();
        envelope.Status = EnvelopeStatus.Outgoing;

        await Stores.Outbox.StoreOutgoingAsync(envelope, 11);
        await Stores.Outbox.DeleteOutgoingAsync(envelope);

        var database2 = await Stores.GetDatabaseAsync("tenant2");
        var envelopes = await database2.Admin.AllOutgoingAsync();
        var loaded = envelopes.FirstOrDefault(x => x.Id == envelope.Id);
        loaded.ShouldBeNull();
    }

    [Fact]
    public async Task delete_outgoing_batch_all_one_tenant()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        foreach (var envelope in envelopes)
        {
            await Stores.Outbox.StoreOutgoingAsync(envelope, 3);
        }

        var fromDefault = envelopes.Where(x => x.TenantId.IsEmpty()).ToArray();
        var from1 = envelopes.Where(x => x.TenantId == "tenant1").ToArray();

        (await Stores.Master.Admin.FetchCountsAsync()).Outgoing.ShouldBeGreaterThan(0);

        await Stores.Outbox.DeleteOutgoingAsync(fromDefault);

        (await Stores.Master.Admin.FetchCountsAsync()).Outgoing.ShouldBe(0);

        var database1 = await Stores.GetDatabaseAsync("tenant1");

        (await database1.Admin.FetchCountsAsync()).Outgoing.ShouldBeGreaterThan(0);

        await database1.Outbox.DeleteOutgoingAsync(from1);

        (await database1.Admin.FetchCountsAsync()).Outgoing.ShouldBe(0);
    }

    [Fact]
    public async Task delete_outgoing_from_mixed_bag_of_tenants()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        foreach (var envelope in envelopes)
        {
            await Stores.Outbox.StoreOutgoingAsync(envelope, 3);
        }

        var fromMaster = envelopes.FirstOrDefault(x => x.TenantId.IsEmpty());
        var from1 = envelopes.FirstOrDefault(x => x.TenantId == "tenant1");
        var from2 = envelopes.FirstOrDefault(x => x.TenantId == "tenant2");
        var from3 = envelopes.FirstOrDefault(x => x.TenantId == "tenant3");

        await Stores.Outbox.DeleteOutgoingAsync([fromMaster, from1, from2, from3]);

        var all = await Stores.Admin.AllOutgoingAsync();

        all.Count.ShouldBe(envelopes.Count - 4);

        all.ShouldNotContain(x => x.Id == fromMaster.Id);
        all.ShouldNotContain(x => x.Id == from1.Id);
        all.ShouldNotContain(x => x.Id == from2.Id);
        all.ShouldNotContain(x => x.Id == from3.Id);
    }

    [Fact]
    public async Task move_to_dead_letter_queue()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        var fromMaster = envelopes.FirstOrDefault(x => x.TenantId.IsEmpty());
        var from1 = envelopes.FirstOrDefault(x => x.TenantId == "tenant1");
        var from2 = envelopes.FirstOrDefault(x => x.TenantId == "tenant2");
        var from3 = envelopes.FirstOrDefault(x => x.TenantId == "tenant3");


        await Stores.Inbox.MoveToDeadLetterStorageAsync(fromMaster, new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(from1, new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(from2, new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(from3, new NotSupportedException());

        var db1 = await Stores.GetDatabaseAsync("tenant1");
        var db2 = await Stores.GetDatabaseAsync("tenant2");
        var db3 = await Stores.GetDatabaseAsync("tenant3");

        (await Stores.Master.DeadLetters.DeadLetterEnvelopeByIdAsync(fromMaster.Id)).ShouldNotBeNull();
        (await db1.DeadLetters.DeadLetterEnvelopeByIdAsync(from1.Id)).ShouldNotBeNull();
        (await db2.DeadLetters.DeadLetterEnvelopeByIdAsync(from2.Id)).ShouldNotBeNull();
        (await db3.DeadLetters.DeadLetterEnvelopeByIdAsync(from3.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task load_dead_letter_envelopes_across_databases()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        var fromMaster = envelopes.FirstOrDefault(x => x.TenantId.IsEmpty());
        var from1 = envelopes.FirstOrDefault(x => x.TenantId == "tenant1");
        var from2 = envelopes.FirstOrDefault(x => x.TenantId == "tenant2");
        var from3 = envelopes.FirstOrDefault(x => x.TenantId == "tenant3");


        await Stores.Inbox.MoveToDeadLetterStorageAsync(fromMaster, new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(from1, new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(from2, new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(from3, new NotSupportedException());

        (await Stores.DeadLetterEnvelopeByIdAsync(fromMaster.Id)).ShouldNotBeNull();
        (await Stores.DeadLetterEnvelopeByIdAsync(from1.Id)).ShouldNotBeNull();
        (await Stores.DeadLetterEnvelopeByIdAsync(from2.Id)).ShouldNotBeNull();
        (await Stores.DeadLetterEnvelopeByIdAsync(from3.Id)).ShouldNotBeNull();
    }

    [Fact]
    public async Task MoveToDeadLetterStorageAsync_smoke_test()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        await Stores.Inbox.StoreIncomingAsync(envelopes);

        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[1], new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[5], new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[6], new NotSupportedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[12], new NotSupportedException());

        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[2], new NotImplementedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[3], new NotImplementedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[7], new NotImplementedException());
        await Stores.Inbox.MoveToDeadLetterStorageAsync(envelopes[15], new NotImplementedException());

        await Stores.DeadLetters.MarkDeadLetterEnvelopesAsReplayableAsync(typeof(NotImplementedException).FullName);
    }

    [Fact]
    public async Task discard_and_reassign()
    {
        var envelopes = new List<Envelope>();
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant1"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant2"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor("tenant3"));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));
        envelopes.Add(envelopeFor(null));

        foreach (var envelope in envelopes)
        {
            await Stores.Outbox.StoreOutgoingAsync(envelope, 3);
        }

        var discards = new Envelope[] { envelopes[0], envelopes[1], envelopes[2], envelopes[18] };
        var reassigns = new Envelope[] { envelopes[3], envelopes[5] };

        await Stores.Outbox.DiscardAndReassignOutgoingAsync(discards, reassigns, 13);

        var outgoing = await Stores.Admin.AllOutgoingAsync();

        outgoing.Count.ShouldBe(envelopes.Count - discards.Length);

        foreach (var discard in discards)
        {
            outgoing.ShouldNotContain(x => x.Id == discard.Id);
        }

        foreach (var reassign in reassigns)
        {
            var loaded = outgoing.FirstOrDefault(x => x.Id == reassign.Id);
            loaded.OwnerId.ShouldBe(13);
        }
    }
}