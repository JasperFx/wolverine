using Xunit;

namespace CoreTests.ErrorHandling.Faults;

public class ExceptionInfoTests
{
    [Fact]
    public void from_simple_exception_captures_type_and_message()
    {
        var ex = new InvalidOperationException("boom");

        var info = ExceptionInfo.From(ex);

        info.Type.ShouldBe(typeof(InvalidOperationException).FullName);
        info.Message.ShouldBe("boom");
        info.StackTrace.ShouldBeNull();
        info.InnerExceptions.ShouldBeEmpty();
    }

    [Fact]
    public void from_exception_with_stack_trace()
    {
        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { captured = ex; }

        var info = ExceptionInfo.From(captured);

        info.StackTrace.ShouldNotBeNull();
        info.StackTrace.ShouldContain("from_exception_with_stack_trace");
    }

    [Fact]
    public void from_exception_with_inner_exception_recurses()
    {
        var inner = new ArgumentNullException("name");
        var outer = new InvalidOperationException("outer", inner);

        var info = ExceptionInfo.From(outer);

        info.InnerExceptions.Count.ShouldBe(1);
        info.InnerExceptions[0].Type.ShouldBe(typeof(ArgumentNullException).FullName);
        info.InnerExceptions[0].Message.ShouldBe(inner.Message);
    }

    [Fact]
    public void from_aggregate_exception_lists_all_inners()
    {
        var a = new InvalidOperationException("a");
        var b = new ArgumentException("b");
        var agg = new AggregateException(a, b);

        var info = ExceptionInfo.From(agg);

        info.Type.ShouldBe(typeof(AggregateException).FullName);
        info.InnerExceptions.Count.ShouldBe(2);
        info.InnerExceptions[0].Message.ShouldBe("a");
        info.InnerExceptions[1].Message.ShouldBe("b");
    }
}
