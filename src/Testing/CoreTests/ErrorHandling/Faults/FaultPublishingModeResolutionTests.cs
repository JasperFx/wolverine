using Wolverine.ErrorHandling;
using Xunit;

namespace CoreTests.ErrorHandling.Faults;

public class FaultPublishingModeResolutionTests
{
    private record Foo;
    private record Bar;

    private static FaultPublishingPolicy GetPolicy(WolverineOptions opts) => opts.FaultPublishing;

    [Fact]
    public void global_off_no_override_resolves_none()
    {
        var opts = new WolverineOptions();

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.None);
    }

    [Fact]
    public void per_type_publish_fault_overrides_off_global_to_dlq_only()
    {
        var opts = new WolverineOptions();
        opts.Policies.ForMessagesOfType<Foo>().PublishFault();

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.DlqOnly);
    }

    [Fact]
    public void per_type_publish_fault_with_include_discarded_overrides_to_dlq_and_discard()
    {
        var opts = new WolverineOptions();
        opts.Policies.ForMessagesOfType<Foo>().PublishFault(includeDiscarded: true);

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.DlqAndDiscard);
    }

    [Fact]
    public void global_on_inherits_dlq_only()
    {
        var opts = new WolverineOptions();
        opts.PublishFaultEvents();

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.DlqOnly);
    }

    [Fact]
    public void global_on_with_include_discarded_inherits_dlq_and_discard()
    {
        var opts = new WolverineOptions();
        opts.PublishFaultEvents(includeDiscarded: true);

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.DlqAndDiscard);
    }

    [Fact]
    public void per_type_do_not_publish_fault_overrides_global_on_to_none()
    {
        var opts = new WolverineOptions();
        opts.PublishFaultEvents();
        opts.Policies.ForMessagesOfType<Foo>().DoNotPublishFault();

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.None);
    }

    [Fact]
    public void per_type_publish_fault_narrows_global_dlq_and_discard_to_dlq_only()
    {
        var opts = new WolverineOptions();
        opts.PublishFaultEvents(includeDiscarded: true);
        opts.Policies.ForMessagesOfType<Foo>().PublishFault();

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.DlqOnly);
    }

    [Fact]
    public void unrelated_type_inherits_global()
    {
        var opts = new WolverineOptions();
        opts.PublishFaultEvents();
        opts.Policies.ForMessagesOfType<Foo>().DoNotPublishFault();

        GetPolicy(opts).Resolve(typeof(Bar)).Mode.ShouldBe(FaultPublishingMode.DlqOnly);
    }

    [Fact]
    public void calling_publish_fault_twice_is_last_write_wins()
    {
        var opts = new WolverineOptions();
        opts.Policies.ForMessagesOfType<Foo>().PublishFault();
        opts.Policies.ForMessagesOfType<Foo>().PublishFault(includeDiscarded: true);

        GetPolicy(opts).Resolve(typeof(Foo)).Mode.ShouldBe(FaultPublishingMode.DlqAndDiscard);
    }
}
