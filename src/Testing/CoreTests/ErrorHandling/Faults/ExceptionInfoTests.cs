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

    [Fact]
    public void from_with_default_args_includes_message_and_stacktrace()
    {
        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { captured = ex; }

        var info = ExceptionInfo.From(captured);

        info.Type.ShouldBe(typeof(InvalidOperationException).FullName);
        info.Message.ShouldBe("boom");
        info.StackTrace.ShouldNotBeNull();
    }

    [Fact]
    public void from_with_includeMessage_false_clears_message_keeps_type_and_stacktrace()
    {
        Exception captured;
        try { throw new InvalidOperationException("secret-canary"); }
        catch (Exception ex) { captured = ex; }

        var info = ExceptionInfo.From(captured, includeMessage: false);

        info.Type.ShouldBe(typeof(InvalidOperationException).FullName);
        info.Message.ShouldBe(string.Empty);
        info.StackTrace.ShouldNotBeNull();
    }

    [Fact]
    public void from_with_includeStackTrace_false_clears_stacktrace_keeps_type_and_message()
    {
        Exception captured;
        try { throw new InvalidOperationException("boom"); }
        catch (Exception ex) { captured = ex; }

        var info = ExceptionInfo.From(captured, includeStackTrace: false);

        info.Type.ShouldBe(typeof(InvalidOperationException).FullName);
        info.Message.ShouldBe("boom");
        info.StackTrace.ShouldBeNull();
    }

    [Fact]
    public void from_with_both_flags_false_keeps_only_type()
    {
        Exception captured;
        try { throw new InvalidOperationException("secret"); }
        catch (Exception ex) { captured = ex; }

        var info = ExceptionInfo.From(captured, includeMessage: false, includeStackTrace: false);

        info.Type.ShouldBe(typeof(InvalidOperationException).FullName);
        info.Message.ShouldBe(string.Empty);
        info.StackTrace.ShouldBeNull();
    }

    [Fact]
    public void from_recursion_propagates_flags_to_inner_exception()
    {
        var inner = new ArgumentException("inner-secret");
        var outer = new InvalidOperationException("outer-secret", inner);

        var info = ExceptionInfo.From(outer, includeMessage: false);

        info.Message.ShouldBe(string.Empty);
        info.InnerExceptions.Count.ShouldBe(1);
        info.InnerExceptions[0].Type.ShouldBe(typeof(ArgumentException).FullName);
        info.InnerExceptions[0].Message.ShouldBe(string.Empty);
    }

    [Fact]
    public void from_recursion_propagates_flags_to_aggregate_inner_exceptions()
    {
        var a = new InvalidOperationException("secret-a");
        var b = new ArgumentException("secret-b");
        var agg = new AggregateException(a, b);

        var info = ExceptionInfo.From(agg, includeMessage: false);

        info.Type.ShouldBe(typeof(AggregateException).FullName);
        info.InnerExceptions.Count.ShouldBe(2);
        info.InnerExceptions[0].Message.ShouldBe(string.Empty);
        info.InnerExceptions[1].Message.ShouldBe(string.Empty);
    }
}
