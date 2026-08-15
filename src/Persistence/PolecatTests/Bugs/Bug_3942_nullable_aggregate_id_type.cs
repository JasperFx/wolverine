using Shouldly;
using Wolverine.Polecat.Persistence.Sagas;
using Wolverine.Polecat.Requirements;

namespace PolecatTests.Bugs;

/// <summary>
///     GH-3942: the two stores have to agree on an aggregate's id type.
/// </summary>
/// <remarks>
///     <c>WriteModelAttribute.Modify</c> asks the provider for the aggregate's id type and
///     <c>FindIdentity</c> branches on whether the answer is primitive. Marten answers with the
///     configured document id type, which is never nullable; Polecat reflected the <c>Id</c>
///     property verbatim, so <c>public AlertId? Id { get; set; }</c> answered
///     <c>Nullable&lt;AlertId&gt;</c>. That is not primitive, so the <c>IdentifiedBy&lt;T&gt;</c>
///     escape hatch was skipped and the message was scanned for a <c>Nullable&lt;AlertId&gt;</c>
///     member — which exists in no codebase. Every such chain failed to build under Polecat while
///     the identical shared source worked under Marten.
///     <para>
///     These are deliberately reflection-level rather than integration tests: the failure is at
///     codegen time, and under <c>TypeLoadMode.Dynamic</c> a chain only fails when its message first
///     arrives — so an integration test would have to dispatch every message type to see it.
///     </para>
/// </remarks>
public class Bug_3942_nullable_aggregate_id_type
{
    private static Type sagaIdType<T>() =>
        new PolecatPersistenceFrameProvider().DetermineSagaIdType(typeof(T), null!);

    // ===== The reported case =====

    [Fact]
    public void nullable_strong_typed_id_is_unwrapped()
    {
        sagaIdType<NullableStrongTypedIdAggregate>().ShouldBe(typeof(AlertId));
    }

    [Fact]
    public void nullable_primitive_id_is_unwrapped()
    {
        sagaIdType<NullableGuidAggregate>().ShouldBe(typeof(Guid));
    }

    // ===== Everything else is untouched =====

    [Fact]
    public void non_nullable_strong_typed_id_is_unchanged()
    {
        sagaIdType<StrongTypedIdAggregate>().ShouldBe(typeof(AlertId));
    }

    [Fact]
    public void non_nullable_primitive_id_is_unchanged()
    {
        sagaIdType<GuidAggregate>().ShouldBe(typeof(Guid));
    }

    // A string id is already a reference type, so it was never wrapped -- and `string?` erases to
    // string at runtime, which is exactly why the bug only bit value-type ids.
    [Fact]
    public void string_id_is_unchanged()
    {
        sagaIdType<StringAggregate>().ShouldBe(typeof(string));
    }

    [Fact]
    public void a_type_with_no_id_property_still_falls_back_to_guid()
    {
        sagaIdType<NoIdAggregate>().ShouldBe(typeof(Guid));
    }

    // ===== DocumentExistsAttribute.ResolveIdType is kept in lockstep =====

    [Fact]
    public void document_id_resolution_agrees_with_the_saga_provider()
    {
        DocumentExistsAttribute<NullableStrongTypedIdAggregate>
            .ResolveIdType(typeof(NullableStrongTypedIdAggregate)).ShouldBe(typeof(AlertId));

        DocumentExistsAttribute<NullableGuidAggregate>
            .ResolveIdType(typeof(NullableGuidAggregate)).ShouldBe(typeof(Guid));

        DocumentExistsAttribute<GuidAggregate>
            .ResolveIdType(typeof(GuidAggregate)).ShouldBe(typeof(Guid));

        DocumentExistsAttribute<NoIdAggregate>
            .ResolveIdType(typeof(NoIdAggregate)).ShouldBe(typeof(Guid));
    }
}

public readonly record struct AlertId(string Value);

public class NullableStrongTypedIdAggregate
{
    public AlertId? Id { get; set; }
}

public class StrongTypedIdAggregate
{
    public AlertId Id { get; set; }
}

public class NullableGuidAggregate
{
    public Guid? Id { get; set; }
}

public class GuidAggregate
{
    public Guid Id { get; set; }
}

public class StringAggregate
{
    public string Id { get; set; } = string.Empty;
}

public class NoIdAggregate
{
    public string Name { get; set; } = string.Empty;
}
