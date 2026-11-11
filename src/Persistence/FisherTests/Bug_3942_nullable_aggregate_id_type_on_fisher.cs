using Shouldly;
using Wolverine.Fisher.Persistence.Sagas;

namespace FisherTests;

/// <summary>
///     GH-3942, ported to Fisher by GH-5142: every store has to agree on an aggregate's id type. Polecat got
///     the <c>Nullable&lt;T&gt;</c> unwrap and Fisher, a near copy of Polecat's integration, did not.
/// </summary>
/// <remarks>
///     <c>WriteModelAttribute.Modify</c> asks the provider for the aggregate's id type and
///     <c>FindIdentity</c> branches on whether the answer is primitive. Marten answers with the
///     configured document id type, which is never nullable; Fisher reflected the <c>Id</c>
///     property verbatim, so <c>public FiAlertId? Id { get; set; }</c> answered
///     <c>Nullable&lt;FiAlertId&gt;</c>. That is not primitive, so the <c>IdentifiedBy&lt;T&gt;</c>
///     escape hatch was skipped and the message was scanned for a <c>Nullable&lt;FiAlertId&gt;</c>
///     member — which exists in no codebase. Every such chain failed to build under Fisher while
///     the identical shared source worked under Marten and Polecat.
///     <para>
///     These are deliberately reflection-level rather than integration tests: the failure is at
///     codegen time, and under <c>TypeLoadMode.Dynamic</c> a chain only fails when its message first
///     arrives — so an integration test would have to dispatch every message type to see it.
///     </para>
/// </remarks>
public class Bug_3942_nullable_aggregate_id_type_on_fisher
{
    private static Type sagaIdType<T>() =>
        new FisherPersistenceFrameProvider().DetermineSagaIdType(typeof(T), null!);

    // ===== The reported case =====

    [Fact]
    public void nullable_strong_typed_id_is_unwrapped()
    {
        sagaIdType<FiNullableStrongTypedIdAggregate>().ShouldBe(typeof(FiAlertId));
    }

    [Fact]
    public void nullable_primitive_id_is_unwrapped()
    {
        sagaIdType<FiNullableGuidAggregate>().ShouldBe(typeof(Guid));
    }

    // ===== Everything else is untouched =====

    [Fact]
    public void non_nullable_strong_typed_id_is_unchanged()
    {
        sagaIdType<FiStrongTypedIdAggregate>().ShouldBe(typeof(FiAlertId));
    }

    [Fact]
    public void non_nullable_primitive_id_is_unchanged()
    {
        sagaIdType<FiGuidAggregate>().ShouldBe(typeof(Guid));
    }

    // A string id is already a reference type, so it was never wrapped -- and `string?` erases to
    // string at runtime, which is exactly why the bug only bit value-type ids.
    [Fact]
    public void string_id_is_unchanged()
    {
        sagaIdType<FiStringAggregate>().ShouldBe(typeof(string));
    }

    [Fact]
    public void a_type_with_no_id_property_still_falls_back_to_guid()
    {
        sagaIdType<FiNoIdAggregate>().ShouldBe(typeof(Guid));
    }
}

public readonly record struct FiAlertId(string Value);

public class FiNullableStrongTypedIdAggregate
{
    public FiAlertId? Id { get; set; }
}

public class FiStrongTypedIdAggregate
{
    public FiAlertId Id { get; set; }
}

public class FiNullableGuidAggregate
{
    public Guid? Id { get; set; }
}

public class FiGuidAggregate
{
    public Guid Id { get; set; }
}

public class FiStringAggregate
{
    public string Id { get; set; } = string.Empty;
}

public class FiNoIdAggregate
{
    public string Name { get; set; } = string.Empty;
}
