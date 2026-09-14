using IntegrationTests;
using Marten;
using Shouldly;

namespace MartenTests.AncillaryStores;

/// <summary>
///     GH-4441, investigation. <c>MartenPersistenceFrameProvider.DetermineSagaIdType</c> resolves an identity
///     type through <c>container.GetInstance&lt;IDocumentStore&gt;()</c> — always the DEFAULT store, never the
///     ancillary store a chain was routed to. This probe asks the question the issue turns on: <b>can two
///     stores disagree about the same type's id?</b>
/// </summary>
/// <remarks>
///     <para>
///     Nothing here starts a host or touches a database. <c>FindOrResolveDocumentType</c> goes through
///     <c>Storage.FindMapping</c>, which <b>auto-creates a mapping by convention</b> for a type the store has
///     never been told about — so the default store always answers, rather than failing, which is what makes
///     the store-blindness silent rather than loud.
///     </para>
///     <para>
///     If these two answers are always equal, the blindness is unreachable and the issue closes with a
///     comment. If they can differ, the next step is a handler-level test proving user-visible damage.
///     </para>
/// </remarks>
public class saga_id_type_across_stores_4441
{
    private static Type idTypeFor(IDocumentStore store, Type documentType)
        => store.Options.FindOrResolveDocumentType(documentType).IdType;

    [Fact]
    public void conventional_registration_agrees_across_stores()
    {
        // The ordinary case: neither store is told anything about the type, so both resolve the same
        // conventional mapping off the Id property. This is the case that makes the blindness harmless
        // for virtually every real aggregate.
        using var main = DocumentStore.For(o => o.Connection(Servers.PostgresConnectionString));
        using var ancillary = DocumentStore.For(o =>
        {
            o.Connection(Servers.PostgresConnectionString);
            o.DatabaseSchemaName = "saga_id_probe";
        });

        idTypeFor(main, typeof(SagaIdProbeOrder)).ShouldBe(typeof(Guid));
        idTypeFor(ancillary, typeof(SagaIdProbeOrder)).ShouldBe(typeof(Guid));
    }

    [Fact]
    public void a_per_store_identity_override_makes_the_two_stores_disagree()
    {
        // The only mechanism I can find that diverges: the ancillary store explicitly names a different
        // member as the identity. Plain, supported Marten configuration.
        using var main = DocumentStore.For(o => o.Connection(Servers.PostgresConnectionString));
        using var ancillary = DocumentStore.For(o =>
        {
            o.Connection(Servers.PostgresConnectionString);
            o.DatabaseSchemaName = "saga_id_probe";
            o.Schema.For<SagaIdProbeOrder>().Identity(x => x.Code);
        });

        // The default store, which has never heard of this type, still answers -- with the conventional
        // Guid Id. That is the answer DetermineSagaIdType hands the aggregate handler workflow today.
        idTypeFor(main, typeof(SagaIdProbeOrder)).ShouldBe(typeof(Guid));

        // ...while the store the work would actually commit through says string.
        idTypeFor(ancillary, typeof(SagaIdProbeOrder)).ShouldBe(typeof(string));
    }
}

public class SagaIdProbeOrder
{
    public Guid Id { get; set; }
    public string Code { get; set; } = string.Empty;
}
