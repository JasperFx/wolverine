using FailureGroupSamples;
using JasperFx.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Raven.Client.Documents;
using Raven.Client.Documents.Operations;
using Raven.Client.Documents.Queries;
using Shouldly;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.RavenDb;
using Wolverine.RavenDb.Internals;

namespace RavenDbTests;

[Collection("raven")]
public class dead_letter_failure_group_compliance : DeadLetterFailureGroupCompliance
{
    private readonly DatabaseFixture _fixture;
    private IDocumentStore _store = null!;

    public dead_letter_failure_group_compliance(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    public override async Task<IHost> BuildCleanHost()
    {
        _store = _fixture.StartRavenStore();

        return await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Durability.Mode = DurabilityMode.Solo;

                opts.UseRavenDbPersistence();
                opts.Services.AddSingleton(_store);
            })
            .ConfigureServices(services =>
            {
                // Replayable dead letters would otherwise be moved by the data subscription mid-test
                var replayer = services.Where(x => x.ImplementationType == typeof(DeadLetterQueueReplayer)).ToArray();
                foreach (var descriptor in replayer) services.Remove(descriptor);
            })
            .StartAsync();
    }

    [Fact]
    public async Task documents_written_before_archiving_existed_are_not_archived()
    {
        await deadLetters(3, i => new InvalidOperationException(i.ToString()));

        // Documents stored by an earlier version have no ArchivedAt field at all
        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from DeadLetterMessages as m update { delete this.ArchivedAt; delete this.FailureGroup; }",
            WaitForNonStaleResults = true
        }), token: TestContext.Current.CancellationToken);
        await op.WaitForCompletionAsync();

        (await all()).Count.ShouldBe(3);
        (await all(new DeadLetterEnvelopeQuery { Archived = DeadLetterArchiveFilter.Only })).ShouldBeEmpty();

        // ...and no failure group either
        var legacy = DeadLetterFingerprint.UnfingerprintedGroupFor("System.InvalidOperationException");
        (await all(new DeadLetterEnvelopeQuery { FailureGroup = legacy })).Count.ShouldBe(3);

        (await theDeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery(), TestContext.Current.CancellationToken))
            .Affected.ShouldBe(3);

        (await theDeadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery(), TestContext.Current.CancellationToken))
            .Affected.ShouldBe(3);
        (await all()).ShouldBeEmpty();
    }

    protected override async Task clearFailureGroupsAsync()
    {
        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from DeadLetterMessages as m update { m.FailureGroup = null; m.FailureTitle = null; }",
            WaitForNonStaleResults = true
        }));

        await op.WaitForCompletionAsync();
    }

    protected override async Task clearFailedAtAsync()
    {
        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from DeadLetterMessages as m update { delete this.FailedAt; }",
            WaitForNonStaleResults = true
        }));

        await op.WaitForCompletionAsync();
    }

    [Fact]
    public async Task a_dead_letter_with_a_null_failure_time_falls_back_to_the_sent_time()
    {
        theGenerator.StartingTime = DateTimeOffset.UtcNow.AddHours(-2);
        await deadLetter(Failures.CardDeclined("recent"));
        theGenerator.StartingTime = DateTimeOffset.UtcNow.AddDays(-10);
        await deadLetter(Failures.CardDeclined("old"));

        var op = await _store.Operations.SendAsync(new PatchByQueryOperation(new IndexQuery
        {
            Query = "from DeadLetterMessages as m update { m.FailedAt = null; }",
            WaitForNonStaleResults = true
        }), token: TestContext.Current.CancellationToken);
        await op.WaitForCompletionAsync();

        var query = new DeadLetterEnvelopeQuery(new TimeRange(DateTimeOffset.UtcNow.AddDays(-1), null))
            { RangeOn = DeadLetterTimeField.FailedAt };
        var found = await all(query);
        found.Count.ShouldBe(1);
        found[0].ExceptionMessage.ShouldContain("recent");
    }
}
