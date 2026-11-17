using System.Text.Json;
using Wolverine.Runtime.Serialization;
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

    [Fact]
    public void from_truncates_at_max_depth()
    {
        Exception inner = new InvalidOperationException("level-0");
        for (int i = 1; i < 50; i++)
        {
            inner = new InvalidOperationException($"level-{i}", inner);
        }

        var info = ExceptionInfo.From(inner);

        var current = info;
        for (int i = 0; i < 32; i++)
        {
            current.InnerExceptions.Count.ShouldBe(1);
            current = current.InnerExceptions[0];
        }

        current.Type.ShouldBe("__truncated__");
        current.Message.ShouldBe("[Inner exception chain truncated at depth 32]");
        current.StackTrace.ShouldBeNull();
        current.InnerExceptions.ShouldBeEmpty();
    }

    [Fact]
    public void from_captures_the_type_hierarchy_most_derived_first_stopping_before_exception()
    {
        var info = ExceptionInfo.From(new OutOfStockException("sku-1"));

        info.TypeHierarchy.ShouldBe([
            typeof(OutOfStockException).FullName!,
            typeof(InventoryException).FullName!
        ]);
    }

    [Fact]
    public void from_captures_the_type_hierarchy_of_inner_exceptions()
    {
        var info = ExceptionInfo.From(new InvalidOperationException("outer", new OutOfStockException("sku-1")));

        info.TypeHierarchy.ShouldBe([
            typeof(InvalidOperationException).FullName!,
            typeof(SystemException).FullName!
        ]);
        info.InnerExceptions[0].TypeHierarchy.ShouldBe([
            typeof(OutOfStockException).FullName!,
            typeof(InventoryException).FullName!
        ]);
    }

    [Fact]
    public void redaction_flags_keep_the_type_hierarchy()
    {
        var info = ExceptionInfo.From(new OutOfStockException("sku-1"), includeMessage: false,
            includeStackTrace: false);

        info.TypeHierarchy.Count.ShouldBe(2);
        info.Is<InventoryException>().ShouldBeTrue();
    }

    #region sample_matching_a_remote_exception_by_base_type

    [Fact]
    public void match_a_fault_by_its_exception_type_or_a_base_type()
    {
        var info = ExceptionInfo.From(new OutOfStockException("sku-1"));

        // The thrown type, any base type, and System.Exception all match
        info.Is<OutOfStockException>().ShouldBeTrue();
        info.Is<InventoryException>().ShouldBeTrue();
        info.Is<Exception>().ShouldBeTrue();

        // By full type name, for a receiver that cannot reference the sender's exception types
        info.Is("CoreTests.ErrorHandling.Faults.InventoryException").ShouldBeTrue();

        // Unrelated types do not
        info.Is<InvalidOperationException>().ShouldBeFalse();
        info.Is<SomeOtherInventoryException>().ShouldBeFalse();
    }

    #endregion

    [Fact]
    public void a_fault_from_an_older_sender_has_no_hierarchy_and_matches_its_own_type_only()
    {
        // What a Wolverine 6.x sender writes: no typeHierarchy property at all
        var json = """
                   {"type":"CoreTests.ErrorHandling.Faults.OutOfStockException","message":"sku-1","stackTrace":null,"innerExceptions":[]}
                   """;

        var info = JsonSerializer.Deserialize<ExceptionInfo>(json, SystemTextJsonSerializer.DefaultOptions())!;

        info.TypeHierarchy.ShouldBeEmpty();
        info.Is<OutOfStockException>().ShouldBeTrue();
        info.Is<InventoryException>().ShouldBeFalse();
        info.Is<Exception>().ShouldBeTrue();
    }

    [Fact]
    public void the_type_hierarchy_survives_a_round_trip_through_the_default_serializer()
    {
        var serializer = new SystemTextJsonSerializer(SystemTextJsonSerializer.DefaultOptions());
        var fault = new Fault<FaultedMessage>(new FaultedMessage("sku-1"),
            ExceptionInfo.From(new InvalidOperationException("outer", new OutOfStockException("sku-1"))),
            Attempts: 3, FailedAt: DateTimeOffset.UtcNow, CorrelationId: null, ConversationId: Guid.NewGuid(),
            TenantId: null, Source: "tests", Headers: new Dictionary<string, string?>());

        var envelope = new Envelope { Data = serializer.WriteMessage(fault) };
        var copy = (Fault<FaultedMessage>)serializer.ReadFromData(typeof(Fault<FaultedMessage>), envelope);

        copy.Exception.TypeHierarchy.ShouldBe(fault.Exception.TypeHierarchy);
        copy.Exception.InnerExceptions[0].TypeHierarchy.ShouldBe(fault.Exception.InnerExceptions[0].TypeHierarchy);
        copy.Exception.InnerExceptions[0].Is<InventoryException>().ShouldBeTrue();
    }
}

public record FaultedMessage(string Sku);

public class InventoryException(string message) : Exception(message);

public class OutOfStockException(string sku) : InventoryException($"{sku} is out of stock");

public class SomeOtherInventoryException(string message) : InventoryException(message);
