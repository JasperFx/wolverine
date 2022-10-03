using System;
using Wolverine.ErrorHandling;
using Wolverine.ErrorHandling.Matches;
using Xunit;

namespace CoreTests.ErrorHandling;

public class ExceptionMatchTypeTests
{
    [Fact]
    public void exclude_match()
    {
        var match = new ExcludeType<BadImageFormatException>();
        match.Matches(new Exception()).ShouldBeTrue();
        match.Matches(new BadImageFormatException()).ShouldBeFalse();
    }

    [Fact]
    public void type_match()
    {
        var match = new TypeMatch<BadImageFormatException>();
        match.Matches(new BadImageFormatException()).ShouldBeTrue();
        match.Matches(new DivideByZeroException()).ShouldBeFalse();
    }

    [Fact]
    public void and_match()
    {
        var match = new TypeMatch<BadImageFormatException>()
            .And(new MessageContains("bad"));

        match.Matches(new InvalidOperationException("bad"))
            .ShouldBeFalse();

        match.Matches(new BadImageFormatException("good"))
            .ShouldBeFalse();

        match.Matches(new BadImageFormatException("bad"))
            .ShouldBeTrue();
    }

    [Fact]
    public void or_match()
    {
        var match = new TypeMatch<BadImageFormatException>()
            .Or(new MessageContains("bad"));

        match.Matches(new InvalidOperationException("bad"))
            .ShouldBeTrue();

        match.Matches(new BadImageFormatException("good"))
            .ShouldBeTrue();

        match.Matches(new BadImageFormatException("bad"))
            .ShouldBeTrue();

        match.Matches(new Exception())
            .ShouldBeFalse();
    }

    [Fact]
    public void inner_match()
    {
        var match = new InnerMatch(new TypeMatch<BadImageFormatException>());

        match.Matches(new BadImageFormatException()).ShouldBeFalse();
        match.Matches(new Exception("bad", new DivideByZeroException())).ShouldBeFalse();

        match.Matches(new Exception("bad", new BadImageFormatException())).ShouldBeTrue();
    }
}