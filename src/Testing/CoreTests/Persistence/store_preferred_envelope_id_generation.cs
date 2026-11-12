using MassTransit;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Shouldly;
using Wolverine;
using Wolverine.Persistence;
using Wolverine.Persistence.Durability;
using Wolverine.Tracking;
using Xunit;

namespace CoreTests.Persistence;

/// <summary>
/// GH-4902. When nothing was set explicitly and every message store prefers the same envelope id layout,
/// Wolverine switches to it at startup
/// </summary>
public class store_preferred_envelope_id_generation : IDisposable
{
    public void Dispose()
    {
        Envelope.IdGenerator = Guid.CreateVersion7;
    }

    private static readonly System.Reflection.MethodInfo GuidV7Method =
        typeof(Guid).GetMethod(nameof(Guid.CreateVersion7), Type.EmptyTypes)!;

    private static readonly System.Reflection.MethodInfo NewIdMethod =
        typeof(EnvelopeIds).GetMethod(nameof(EnvelopeIds.NextSequentialGuid), Type.EmptyTypes)!;

    private static IMessageStore storePreferring(EnvelopeIdGeneration? preference)
    {
        var store = Substitute.For<IMessageStore>();
        store.PreferredEnvelopeIdGeneration.Returns(preference);
        return store;
    }

    [Fact]
    public void no_stores_means_no_agreement()
    {
        MessageStoreCollection.AgreedEnvelopeIdGeneration([]).ShouldBeNull();
    }

    [Fact]
    public void every_store_agreeing_wins()
    {
        MessageStoreCollection.AgreedEnvelopeIdGeneration([
            storePreferring(EnvelopeIdGeneration.NewIdSqlServerOrder),
            storePreferring(EnvelopeIdGeneration.NewIdSqlServerOrder)
        ]).ShouldBe(EnvelopeIdGeneration.NewIdSqlServerOrder);
    }

    [Fact]
    public void a_store_without_a_preference_means_no_agreement()
    {
        MessageStoreCollection.AgreedEnvelopeIdGeneration([
            storePreferring(EnvelopeIdGeneration.NewIdSqlServerOrder),
            storePreferring(null)
        ]).ShouldBeNull();
    }

    [Fact]
    public void disagreeing_stores_mean_no_agreement()
    {
        MessageStoreCollection.AgreedEnvelopeIdGeneration([
            storePreferring(EnvelopeIdGeneration.NewIdSqlServerOrder),
            storePreferring(EnvelopeIdGeneration.GuidV7)
        ]).ShouldBeNull();
    }

    [Fact]
    public void the_default_interface_member_has_no_preference()
    {
        IMessageStore store = new NullMessageStore();
        store.PreferredEnvelopeIdGeneration.ShouldBeNull();
    }

    [Fact]
    public void an_explicit_assignment_is_tracked_and_beats_the_store_preference()
    {
        var options = new WolverineOptions();
        options.EnvelopeIdGenerationIsExplicit.ShouldBeFalse();

        options.UseStorePreferredEnvelopeIdGeneration(EnvelopeIdGeneration.NewIdSqlServerOrder);
        options.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewIdSqlServerOrder);
        options.EnvelopeIdGenerationIsExplicit.ShouldBeFalse();

        options.EnvelopeIdGeneration = EnvelopeIdGeneration.NewId;
        options.EnvelopeIdGenerationIsExplicit.ShouldBeTrue();
        options.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId);
    }

    [Fact]
    public async Task an_application_without_message_storage_keeps_the_default()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(_ => { })
            .StartAsync(TestContext.Current.CancellationToken);

        // GH-5197. GuidV7 is the 7.0 default
        host.GetRuntime().Options.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.GuidV7);
        Envelope.IdGenerator.Method.ShouldBe(GuidV7Method);
    }

    [Fact]
    public async Task an_application_without_message_storage_keeps_new_id_after_restoring_the_v6_defaults()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts => opts.RestoreV6Defaults())
            .StartAsync(TestContext.Current.CancellationToken);

        host.GetRuntime().Options.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(NewIdMethod);
    }

    // GH-5197. The default for each mix of message stores. PostgreSQL (and every other store but SQL Server and
    // Polecat) has no preference; SQL Server prefers NewIdSqlServerOrder, so SQL Server alone keeps it, in 7.0 and
    // after RestoreV6Defaults() alike. A mix has no agreement and gets the plain default.
    public static TheoryData<string, EnvelopeIdGeneration?[], bool, EnvelopeIdGeneration> StoreMixes => new()
    {
        { "no store", [], false, EnvelopeIdGeneration.GuidV7 },
        { "no store", [], true, EnvelopeIdGeneration.NewId },
        { "PostgreSQL only", [null], false, EnvelopeIdGeneration.GuidV7 },
        { "PostgreSQL only", [null], true, EnvelopeIdGeneration.NewId },
        { "SQL Server only", [EnvelopeIdGeneration.NewIdSqlServerOrder], false, EnvelopeIdGeneration.NewIdSqlServerOrder },
        { "SQL Server only", [EnvelopeIdGeneration.NewIdSqlServerOrder], true, EnvelopeIdGeneration.NewIdSqlServerOrder },
        { "two SQL Server stores", [EnvelopeIdGeneration.NewIdSqlServerOrder, EnvelopeIdGeneration.NewIdSqlServerOrder], false, EnvelopeIdGeneration.NewIdSqlServerOrder },
        { "PostgreSQL and SQL Server", [null, EnvelopeIdGeneration.NewIdSqlServerOrder], false, EnvelopeIdGeneration.GuidV7 },
        { "PostgreSQL and SQL Server", [null, EnvelopeIdGeneration.NewIdSqlServerOrder], true, EnvelopeIdGeneration.NewId },
    };

    [Theory]
    [MemberData(nameof(StoreMixes))]
    public void the_default_for_each_mix_of_message_stores(string mix, EnvelopeIdGeneration?[] preferences,
        bool restoreV6Defaults, EnvelopeIdGeneration expected)
    {
        var options = new WolverineOptions();
        if (restoreV6Defaults) options.RestoreV6Defaults();

        options.ApplyStorePreferredEnvelopeIdGeneration(preferences.Select(storePreferring).ToArray());

        options.EnvelopeIdGeneration.ShouldBe(expected, mix);
    }

    [Theory]
    [InlineData(EnvelopeIdGeneration.NewId, false)]
    [InlineData(EnvelopeIdGeneration.NewId, true)]
    [InlineData(EnvelopeIdGeneration.GuidV7, false)]
    [InlineData(EnvelopeIdGeneration.GuidV7, true)]
    [InlineData(EnvelopeIdGeneration.NewIdSqlServerOrder, false)]
    [InlineData(EnvelopeIdGeneration.NewIdSqlServerOrder, true)]
    public void an_explicit_setting_always_wins(EnvelopeIdGeneration explicitChoice, bool restoreFirst)
    {
        foreach (var preferences in new[]
                 {
                     Array.Empty<EnvelopeIdGeneration?>(),
                     [null],
                     [EnvelopeIdGeneration.NewIdSqlServerOrder],
                     [null, EnvelopeIdGeneration.NewIdSqlServerOrder]
                 })
        {
            var options = new WolverineOptions();
            if (restoreFirst) options.RestoreV6Defaults();
            options.EnvelopeIdGeneration = explicitChoice;
            if (!restoreFirst) options.RestoreV6Defaults();

            options.ApplyStorePreferredEnvelopeIdGeneration(preferences.Select(storePreferring).ToArray())
                .ShouldBeFalse();
            options.EnvelopeIdGeneration.ShouldBe(explicitChoice);
        }
    }

    [Theory]
    [InlineData(false, EnvelopeIdGeneration.GuidV7)]
    [InlineData(true, EnvelopeIdGeneration.NewId)]
    public void ignoring_the_store_preference_keeps_the_default(bool restoreV6Defaults, EnvelopeIdGeneration expected)
    {
        // optimizeEnvelopeIdsForSqlServer: false, whether RestoreV6Defaults() comes before or after it
        var options = new WolverineOptions();
        options.IgnoreStorePreferredEnvelopeIdGeneration();
        if (restoreV6Defaults) options.RestoreV6Defaults();

        options.ApplyStorePreferredEnvelopeIdGeneration([storePreferring(EnvelopeIdGeneration.NewIdSqlServerOrder)])
            .ShouldBeFalse();
        options.EnvelopeIdGeneration.ShouldBe(expected);
        options.EnvelopeIdGenerationIsExplicit.ShouldBeFalse();
    }
}
