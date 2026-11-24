using JasperFx;
using JasperFx.Events;
using Shouldly;
using Wolverine.Http.Runtime;
using Xunit;

namespace Wolverine.Http.Tests;

/// <summary>
/// A commit can wrap its conflicts in an <see cref="AggregateException" />. Every leaf the 409 mapping answers on its
/// own still counts as a conflict when it arrives wrapped: a duplicate natural key or a stream id collision as much
/// as a failed version check.
/// </summary>
public class conflict_mapping_aggregate_exception_leaves
{
    public class Order;

    [Fact]
    public void wrapped_concurrency_failures_are_a_conflict()
    {
        ConflictMapping.IsConflict(new AggregateException(new ConcurrencyException("stale"))).ShouldBeTrue();
    }

    [Fact]
    public void a_wrapped_duplicate_natural_key_is_a_conflict()
    {
        ConflictMapping.IsConflict(new AggregateException(new DuplicateNaturalKeyException(typeof(Order), "ORD-1")))
            .ShouldBeTrue();
    }

    [Fact]
    public void a_wrapped_stream_id_collision_is_a_conflict()
    {
        ConflictMapping.IsConflict(new AggregateException(new ExistingStreamIdCollisionException(Guid.NewGuid())))
            .ShouldBeTrue();
    }

    [Fact]
    public void a_mix_of_conflicts_nested_any_depth_is_a_conflict()
    {
        var nested = new AggregateException(
            new ConcurrencyException("stale"),
            new AggregateException(new DuplicateNaturalKeyException(typeof(Order), "ORD-1")));

        ConflictMapping.IsConflict(nested).ShouldBeTrue();
    }

    [Fact]
    public void anything_else_among_them_is_not()
    {
        ConflictMapping.IsConflict(new AggregateException(
                new DuplicateNaturalKeyException(typeof(Order), "ORD-1"),
                new InvalidOperationException("boom")))
            .ShouldBeFalse();
    }

    [Fact]
    public void an_empty_aggregate_is_not()
    {
        ConflictMapping.IsConflict(new AggregateException()).ShouldBeFalse();
    }
}
