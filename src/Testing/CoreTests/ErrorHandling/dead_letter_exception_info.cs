using JasperFx.Core.Reflection;
using Shouldly;
using Wolverine.Persistence.Durability;
using Xunit;

namespace CoreTests.ErrorHandling;

public class dead_letter_exception_info
{
    [Fact]
    public void falls_back_to_runtime_type_and_message_for_a_normal_exception()
    {
        var ex = new InvalidOperationException("boom");

        ex.DeadLetterExceptionType().ShouldBe(ex.GetType().FullNameInCode());
        ex.DeadLetterExceptionMessage().ShouldBe("boom");
    }

    [Fact]
    public void honors_marker_to_preserve_type_while_redacting_message()
    {
        var original = new InvalidOperationException("secret PII: alice@example.com");
        Exception redacted = new RedactingException(original);

        // The persisted type stays the ORIGINAL type (so operators can still filter/triage by it),
        // even though the runtime type handed to the store is RedactingException.
        redacted.DeadLetterExceptionType().ShouldBe(original.GetType().FullNameInCode());

        // ...while the message is redacted.
        redacted.DeadLetterExceptionMessage().ShouldBe("redacted");
    }

    [Fact]
    public void null_exception_yields_null_strings()
    {
        ((Exception?)null).DeadLetterExceptionType().ShouldBeNull();
        ((Exception?)null).DeadLetterExceptionMessage().ShouldBeNull();
    }

    private sealed class RedactingException : Exception, IDeadLetterExceptionInfo
    {
        public RedactingException(Exception original) : base("redacted")
        {
            ExceptionType = original.GetType().FullNameInCode();
        }

        public string ExceptionType { get; }
        public string ExceptionMessage => "redacted";
    }
}
