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
        Envelope.IdGenerator = NewId.NextSequentialGuid;
    }

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

        host.GetRuntime().Options.EnvelopeIdGeneration.ShouldBe(EnvelopeIdGeneration.NewId);
        Envelope.IdGenerator.Method.ShouldBe(typeof(NewId).GetMethod(nameof(NewId.NextSequentialGuid), Type.EmptyTypes));
    }
}
