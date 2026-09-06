using IntegrationTests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;
using Wolverine.ComplianceTests;
using Wolverine.Persistence.Durability;
using Wolverine.Postgresql;
using Wolverine.RDBMS;
using Xunit;

namespace PostgresqlTests;

/// <summary>
/// GH-4320. The batched durability inserts used to emit one <c>insert ... values (@p0..@p8);</c> per
/// envelope, so a 100-envelope flush was ~900 parameters and a command whose text grew with the batch.
/// <c>unnest</c> makes it one statement at fixed arity, measured ~1.35x faster on the insert.
///
/// <para>
/// These tests assert the structural property rather than the timing: one command text and one
/// parameter count for every batch size. That is what the correctness suites cannot see, and it is
/// what would silently regress if someone reintroduced a per-envelope loop.
/// </para>
///
/// <para>
/// Note the class name is a historical artifact worth keeping honest: GH-4320 predicted the win would
/// come from Npgsql's <c>MaxAutoPrepare</c> and the SQL Server plan cache finally hitting. It did not.
/// Wolverine never sets <c>Max Auto Prepare</c> and Npgsql defaults it off, and switching it on erases
/// the fixed-arity advantage rather than amplifying it. The win is the smaller command and the
/// parameter count. Stability of the command text is still the right thing to assert -- it is the
/// mechanism -- but not for the reason originally given.
/// </para>
/// </summary>
[Collection("marten")]
public class batched_insert_plan_stability_4320 : PostgresqlContext, IAsyncLifetime
{
    private PostgresqlMessageStore theStore = null!;
    private IHost theHost = null!;

    public async ValueTask InitializeAsync()
    {
        theHost = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.PersistMessagesWithPostgresql(Servers.PostgresConnectionString, "plan_cache_4320");
            }).StartAsync();

        theStore = (PostgresqlMessageStore)theHost.Services
            .GetRequiredService<IMessageStore>();
    }

    public async ValueTask DisposeAsync()
    {
        await theHost.StopAsync();
        theHost.Dispose();
    }

    private static IReadOnlyList<Envelope> incoming(int count)
    {
        var list = new List<Envelope>();
        for (var i = 0; i < count; i++)
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Incoming;
            list.Add(envelope);
        }

        return list;
    }

    private static Envelope[] outgoing(int count)
    {
        return Enumerable.Range(0, count).Select(_ =>
        {
            var envelope = ObjectMother.Envelope();
            envelope.Status = EnvelopeStatus.Outgoing;
            return envelope;
        }).ToArray();
    }

    /// <summary>
    /// The whole point, stated directly: three different batch sizes, one command text. Before
    /// GH-4320 these three differed from each other in both text and parameter count.
    /// </summary>
    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 100)]
    [InlineData(1, 100)]
    public void the_incoming_batch_command_text_does_not_vary_with_batch_size(int a, int b)
    {
        using var first = theStore.TestingOnlyBuildBatchedIncoming(incoming(a));
        using var second = theStore.TestingOnlyBuildBatchedIncoming(incoming(b));

        second.CommandText.ShouldBe(first.CommandText);
    }

    [Theory]
    [InlineData(1, 5)]
    [InlineData(5, 100)]
    [InlineData(1, 100)]
    public void the_outgoing_batch_command_text_does_not_vary_with_batch_size(int a, int b)
    {
        using var first = theStore.TestingOnlyBuildBatchedOutgoing(outgoing(a), 1);
        using var second = theStore.TestingOnlyBuildBatchedOutgoing(outgoing(b), 1);

        second.CommandText.ShouldBe(first.CommandText);
    }

    /// <summary>
    /// Fixed arity is the mechanism, and it is worth asserting separately: it is also what keeps the
    /// batch clear of the SQL Server 2100-parameter ceiling that the per-envelope shape walks towards.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(500)]
    public void the_incoming_batch_uses_one_parameter_per_column_regardless_of_size(int count)
    {
        using var command = theStore.TestingOnlyBuildBatchedIncoming(incoming(count));

        command.Parameters.Count.ShouldBe(9);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(500)]
    public void the_outgoing_batch_uses_one_parameter_per_column_regardless_of_size(int count)
    {
        using var command = theStore.TestingOnlyBuildBatchedOutgoing(outgoing(count), 1);

        command.Parameters.Count.ShouldBe(7);
    }
}
