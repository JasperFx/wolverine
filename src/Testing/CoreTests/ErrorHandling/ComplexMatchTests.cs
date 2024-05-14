using Wolverine.ErrorHandling.Matches;
using Xunit;

namespace CoreTests.ErrorHandling;

public class ComplexMatchTests
{
    private readonly ComplexMatch theMatch = new();

    [Fact]
    public void is_empty()
    {
        theMatch.IsEmpty().ShouldBeTrue();
        theMatch.Includes.Add(new AlwaysMatches());

        theMatch.IsEmpty().ShouldBeFalse();

        theMatch.Includes.Clear();
        theMatch.Excludes.Add(new AlwaysMatches());

        theMatch.IsEmpty().ShouldBeFalse();
    }

    [Fact]
    public void reduce_is_always_when_empty()
    {
        theMatch.Reduce().ShouldBeOfType<AlwaysMatches>();
    }

    [Fact]
    public void reduce_is_itself_if_not_empty()
    {
        theMatch.Includes.Add(new TypeMatch<BadImageFormatException>());

        theMatch.Reduce().ShouldBeSameAs(theMatch);
    }

    [Fact]
    public void include_no_filter()
    {
        theMatch.Include<BadImageFormatException>();
        theMatch.Excludes.Any().ShouldBeFalse();
        theMatch.Includes.Single().ShouldBeOfType<TypeMatch<BadImageFormatException>>();
    }

    [Fact]
    public void exclude_no_filter()
    {
        theMatch.Exclude<BadImageFormatException>();
        theMatch.Includes.Any().ShouldBeFalse();
        theMatch.Excludes.Single().ShouldBeOfType<TypeMatch<BadImageFormatException>>();
    }

    [Fact]
    public void include_with_filter()
    {
        theMatch.Include<BadImageFormatException>(e => e.Message.Contains("foo"));
        theMatch.Excludes.Any().ShouldBeFalse();
        theMatch.Includes.Single().ShouldBeOfType<UserSupplied<BadImageFormatException>>();
    }

    [Fact]
    public void exclude_with_filter()
    {
        theMatch.Exclude<BadImageFormatException>(e => e.Message.Contains("foo"));
        theMatch.Includes.Any().ShouldBeFalse();
        theMatch.Excludes.Single().ShouldBeOfType<UserSupplied<BadImageFormatException>>();
    }

    [Fact]
    public void only_includes()
    {
        theMatch.Include<BadImageFormatException>();
        theMatch.Include<DivideByZeroException>();

        theMatch.Matches(new BadImageFormatException())
            .ShouldBeTrue();

        theMatch.Matches(new DivideByZeroException())
            .ShouldBeTrue();

        theMatch.Matches(new InvalidCastException())
            .ShouldBeFalse();
    }

    [Fact]
    public void only_excludes()
    {
        theMatch.Exclude<BadImageFormatException>();
        theMatch.Exclude<DivideByZeroException>();

        theMatch.Matches(new BadImageFormatException())
            .ShouldBeFalse();

        theMatch.Matches(new DivideByZeroException())
            .ShouldBeFalse();

        theMatch.Matches(new InvalidCastException())
            .ShouldBeTrue();
    }
}