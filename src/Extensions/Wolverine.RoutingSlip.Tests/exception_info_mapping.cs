using Shouldly;
using Xunit;

namespace Wolverine.RoutingSlip.Tests;

public class exception_info_mapping
{
    [Fact]
    public void from_exception_maps_exception_and_inner_exception()
    {
        var inner = new InvalidOperationException("inner boom");
        var exception = new ApplicationException("outer boom", inner);

        var info = ExceptionInfo.From(exception);

        info.ExceptionType.ShouldBe(typeof(ApplicationException).FullName);
        info.Message.ShouldBe("outer boom");
        info.InnerException.ShouldNotBeNull();
        info.InnerException.ExceptionType.ShouldBe(typeof(InvalidOperationException).FullName);
        info.InnerException.Message.ShouldBe("inner boom");
    }

    [Fact]
    public void from_exception_throws_on_null()
    {
        Should.Throw<ArgumentNullException>(() => ExceptionInfo.From(null!));
    }
}
