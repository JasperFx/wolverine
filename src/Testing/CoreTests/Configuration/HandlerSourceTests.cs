using Wolverine.Configuration;
using Xunit;

namespace CoreTests.Configuration;

public class HandlerSourceTests
{
    public class OpenGuy<T>{}
    public interface InterfaceGuy{}
    public abstract class AbstractGuy{}

    [Theory]
    [InlineData(typeof(InternalGuy))]
    [InlineData(typeof(OpenGuy<>))]
    [InlineData(typeof(InterfaceGuy))]
    [InlineData(typeof(AbstractGuy))]
    public void assert_on_invalid_handler_types(Type candidateType)
    {
        var source = new HandlerSource();
        Should.Throw<ArgumentOutOfRangeException>(() =>
        {
            source.IncludeType(candidateType);
        });
    }
}

internal static class InternalGuy{}

