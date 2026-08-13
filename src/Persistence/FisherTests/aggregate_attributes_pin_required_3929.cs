using System.Reflection;
using Shouldly;
using Wolverine.Persistence.EventSourcing;
using Wolverine.Fisher;

namespace FisherTests;

// GH-3929. [WriteAggregate]/[ReadAggregate] predate the nullable-annotation inference that
// [WriteModel]/[ReadModel] now use for the Required default, so they must keep their own unconditional
// default rather than inheriting it. Reflection rather than behaviour because the mechanism IS the
// override - a store added later that forgets it inherits a silent, runtime-only behaviour change.
public class aggregate_attributes_pin_required_3929
{
    private static MethodInfo defaultRequiredOn(Type type)
    {
        return type.GetMethod("DefaultRequired", BindingFlags.Instance | BindingFlags.NonPublic)!;
    }

    [Fact]
    public void write_aggregate_overrides_the_required_default()
    {
        defaultRequiredOn(typeof(WriteAggregateAttribute))
            .DeclaringType.ShouldBe(typeof(WriteAggregateAttribute));
    }

    [Fact]
    public void read_aggregate_overrides_the_required_default()
    {
        defaultRequiredOn(typeof(ReadAggregateAttribute))
            .DeclaringType.ShouldBe(typeof(ReadAggregateAttribute));
    }

    [Fact]
    public void the_core_attributes_still_infer_from_nullability()
    {
        defaultRequiredOn(typeof(WriteModelAttribute)).DeclaringType.ShouldBe(typeof(WriteModelAttribute));
        defaultRequiredOn(typeof(ReadModelAttribute)).DeclaringType.ShouldBe(typeof(ReadModelAttribute));
    }
}
