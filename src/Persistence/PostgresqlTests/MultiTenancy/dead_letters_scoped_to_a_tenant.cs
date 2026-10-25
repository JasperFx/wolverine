using IntegrationTests;
using JasperFx.Resources;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Persistence.Durability.DeadLetterManagement;
using Wolverine.Postgresql;
using Xunit;

namespace PostgresqlTests.MultiTenancy;

// GH-5209. Dead letters are stored per database, so DeadLetterEnvelopeQuery.TenantId routes to the tenant's database
public class dead_letters_scoped_to_a_tenant : MultiTenancyContext
{
    private MultiTenantedMessageStore theStore = null!;

    protected override void configureWolverine(WolverineOptions opts)
    {
        opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "dlq_tenants")
            .RegisterStaticTenants(tenants =>
            {
                tenants.Register("red", tenant1ConnectionString);
                tenants.Register("blue", tenant2ConnectionString);
            });

        opts.Durability.Mode = DurabilityMode.Solo;
        opts.Services.AddResourceSetupOnStartup();
    }

    protected override async Task onStartup()
    {
        await theHost.ResetResourceState();
        theStore = theHost.Services.GetRequiredService<IMessageStore>().ShouldBeOfType<MultiTenantedMessageStore>();

        await deadLetters("red", 2);
        await deadLetters("blue", 3);
        await deadLetters(null, 1);
    }

    private async Task deadLetters(string? tenantId, int count)
    {
        var generator = new EnvelopeGenerator { MessageSource = () => new TargetMessage1(Guid.NewGuid(), 1, "Red") };

        for (var i = 0; i < count; i++)
        {
            var envelope = generator.BuildEnvelope();

            // null is the main database
            envelope.TenantId = tenantId;
            await theStore.Inbox.MoveToDeadLetterStorageAsync(envelope, new InvalidOperationException("boom"));
        }
    }

    [Fact]
    public async Task query_is_scoped_to_the_tenant_database()
    {
        var red = await theStore.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery { TenantId = "red" },
            CancellationToken.None);
        red.TotalCount.ShouldBe(2);

        var blue = await theStore.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery { TenantId = "blue" },
            CancellationToken.None);
        blue.TotalCount.ShouldBe(3);
    }

    [Fact]
    public async Task bulk_operations_are_scoped_to_the_tenant_database()
    {
        var replayed = await theStore.DeadLetters.ReplayMatchingAsync(new DeadLetterEnvelopeQuery { TenantId = "blue" },
            CancellationToken.None);
        replayed.Affected.ShouldBe(3);

        var archived = await theStore.DeadLetters.ArchiveMatchingAsync(new DeadLetterEnvelopeQuery { TenantId = "red" },
            CancellationToken.None);
        archived.Affected.ShouldBe(2);

        var summary = await theStore.DeadLetters.SummarizeAllAsync("service",
            new DeadLetterSummaryOptions { Query = new DeadLetterEnvelopeQuery { TenantId = "blue" } },
            CancellationToken.None);
        summary.Single().Count.ShouldBe(3);
    }

    [Fact]
    public async Task without_a_tenant_the_counted_operations_cover_every_database()
    {
        var summary = await theStore.DeadLetters.SummarizeAllAsync("service", new DeadLetterSummaryOptions(),
            CancellationToken.None);
        summary.Sum(x => x.Count).ShouldBe(6);
        summary.Select(x => x.Database).Distinct().Count().ShouldBe(3);

        var discarded = await theStore.DeadLetters.DiscardMatchingAsync(new DeadLetterEnvelopeQuery(),
            CancellationToken.None);
        discarded.Affected.ShouldBe(6);
    }

    [Fact]
    public async Task an_unscoped_page_query_is_still_not_supported()
    {
        await Should.ThrowAsync<NotSupportedException>(() =>
            theStore.DeadLetters.QueryAsync(new DeadLetterEnvelopeQuery(), CancellationToken.None));
    }
}
